import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';

const PATH_TOOLS = new Set(['read', 'write', 'edit', 'grep', 'find', 'ls']);
const READ_PATH_TOOLS = new Set(['read', 'grep', 'find', 'ls']);
const MUTATING_TOOLS = new Set(['write', 'edit']);
const KNOWN_TOOLS = new Set([...PATH_TOOLS, 'bash']);
const PATH_ARGUMENT_KEYS = new Set(['path', 'filePath', 'directory', 'root', 'paths']);

function decision(action, reason) {
  return { action, reason };
}

function stableSerialize(value, seen = new WeakSet()) {
  if (value === null) return 'null';
  if (typeof value === 'string') return JSON.stringify(value);
  if (typeof value === 'boolean') return value ? 'true' : 'false';
  if (typeof value === 'number') return Number.isFinite(value) ? JSON.stringify(value) : JSON.stringify({ $number: String(value) });
  if (typeof value === 'bigint') return JSON.stringify({ $bigint: value.toString() });
  if (typeof value === 'undefined') return JSON.stringify({ $undefined: true });
  if (typeof value === 'function') return JSON.stringify({ $function: String(value) });
  if (typeof value === 'symbol') return JSON.stringify({ $symbol: String(value) });
  if (seen.has(value)) return JSON.stringify({ $circular: true });

  seen.add(value);
  try {
    if (Array.isArray(value)) return `[${value.map((item) => stableSerialize(item, seen)).join(',')}]`;
    const entries = Object.keys(value)
      .sort()
      .map((key) => `${JSON.stringify(key)}:${stableSerialize(value[key], seen)}`);
    return `{${entries.join(',')}}`;
  } finally {
    seen.delete(value);
  }
}

function sha256(value) {
  return createHash('sha256').update(value, 'utf8').digest('hex');
}

function normalizeRule(value, fallback = 'ask') {
  const normalized = String(value || '').trim().toLowerCase();
  if (normalized === 'allow') return 'allow';
  if (normalized === 'ask') return 'ask';
  return fallback;
}

function pathArguments(args) {
  if (!args || typeof args !== 'object') return [];
  const values = [];
  for (const key of ['path', 'filePath', 'directory', 'root']) {
    if (typeof args[key] === 'string') values.push(args[key]);
  }
  if (Array.isArray(args.paths)) {
    for (const item of args.paths) {
      if (typeof item === 'string') values.push(item);
    }
  }
  return [...new Set(values.map((value) => value.trim()).filter(Boolean))];
}

function nonPathArguments(args) {
  if (!args || typeof args !== 'object' || Array.isArray(args)) return args;
  return Object.fromEntries(Object.entries(args).filter(([key]) => !PATH_ARGUMENT_KEYS.has(key)));
}

function realpathNative(candidate) {
  return fs.realpathSync.native ? fs.realpathSync.native(candidate) : fs.realpathSync(candidate);
}

function isSensitivePath(candidate) {
  const name = path.basename(candidate).toLowerCase();
  const extension = path.extname(name);
  if (name.startsWith('.env')) return true;
  if (['auth.json', 'credentials', 'credentials.json', '.npmrc', '.git-credentials', '.netrc', 'secrets.json', 'secrets.yml', 'secrets.yaml', 'token.json', 'tokens.json', '.ssh'].includes(name)) return true;
  if ((name.startsWith('oauth') || name.startsWith('service-account')) && extension === '.json') return true;
  if (name.includes('mnemonic') || name.includes('seed') || name.includes('wallet')) return true;
  if (['id_rsa', 'id_dsa', 'id_ecdsa', 'id_ed25519'].includes(name)) return true;
  return ['.pem', '.key', '.p12', '.pfx', '.kdbx', '.keystore', '.jks'].includes(extension);
}

function existingEntry(candidate) {
  try {
    fs.lstatSync(candidate);
    return true;
  } catch (error) {
    if (error?.code === 'ENOENT' || error?.code === 'ENOTDIR') return false;
    throw error;
  }
}

function canonicalizeAllowMissing(candidate) {
  let cursor = candidate;
  const missing = [];
  while (!existingEntry(cursor)) {
    const parent = path.dirname(cursor);
    if (parent === cursor) throw new Error(`No existing ancestor for path: ${candidate}`);
    missing.unshift(path.basename(cursor));
    cursor = parent;
  }
  const canonicalParent = realpathNative(cursor);
  return path.resolve(canonicalParent, ...missing);
}

function isWithin(root, candidate) {
  const relative = path.relative(root, candidate);
  return relative === '' || (!relative.startsWith(`..${path.sep}`) && relative !== '..' && !path.isAbsolute(relative));
}

function isWithinTrustedReadRoot(candidate, input) {
  const roots = Array.isArray(input?.trustedReadRoots) ? input.trustedReadRoots : [];
  for (const rootValue of roots) {
    if (typeof rootValue !== 'string' || !rootValue.trim()) continue;
    try {
      const root = realpathNative(path.resolve(rootValue));
      if (isWithin(root, candidate)) return true;
    } catch {
      // Invalid or unavailable trusted roots never fail open.
    }
  }
  return false;
}

export function inspectToolPaths(toolName, args, cwd) {
  const candidates = pathArguments(args);
  if (candidates.length === 0) {
    return { ok: false, outside: true, reason: `${toolName} did not provide a usable path` };
  }

  try {
    if (typeof cwd !== 'string' || !cwd.trim()) throw new Error('workspace path is missing');
    const workspace = realpathNative(path.resolve(cwd));
    let outside = false;
    let sensitive = false;
    const resolvedPaths = [];
    for (const candidate of candidates) {
      if (candidate.includes('\0')) throw new Error('path contains a null byte');
      const resolved = path.resolve(workspace, candidate);
      const canonical = MUTATING_TOOLS.has(toolName)
        ? canonicalizeAllowMissing(resolved)
        : realpathNative(resolved);
      resolvedPaths.push(canonical);
      if (!isWithin(workspace, canonical)) outside = true;
      if (isSensitivePath(resolved) || isSensitivePath(canonical)) sensitive = true;
    }
    return { ok: true, outside, sensitive, workspace, paths: resolvedPaths };
  } catch (error) {
    return {
      ok: false,
      outside: true,
      reason: error instanceof Error ? error.message : String(error),
    };
  }
}

export function buildToolApprovalScope(toolNameValue, args, input = {}) {
  const toolName = String(toolNameValue || '').trim().toLowerCase();
  const scopeTool = encodeURIComponent(toolName || 'unnamed');
  if (toolName === 'bash') {
    const command = typeof args?.command === 'string' ? args.command : String(args?.command ?? '');
    return `v1:bash:command:sha256:${sha256(command)}`;
  }

  if (PATH_TOOLS.has(toolName)) {
    const inspection = inspectToolPaths(toolName, args, input.cwd);
    if (inspection.ok) {
      const canonicalPaths = [...new Set(inspection.paths.map((candidate) => (
        process.platform === 'win32' ? candidate.toLowerCase() : candidate
      )))].sort();
      const parameters = nonPathArguments(args);
      if (MUTATING_TOOLS.has(toolName) || stableSerialize(parameters) !== '{}') {
        return `v1:${scopeTool}:args:sha256:${sha256(stableSerialize({ paths: canonicalPaths, parameters }))}`;
      }
      return `v1:${scopeTool}:paths:sha256:${sha256(stableSerialize(canonicalPaths))}`;
    }
  }

  return `v1:${scopeTool}:args:sha256:${sha256(stableSerialize(args))}`;
}

export function decideToolPolicy(toolNameValue, args, input = {}) {
  const toolName = String(toolNameValue || '').trim().toLowerCase();
  const mode = String(input.approvalMode || 'default').trim().toLowerCase();

  if (mode === 'read-only' && !READ_PATH_TOOLS.has(toolName)) {
    return decision('deny', `Tool ${toolName || 'unnamed tool'} is blocked by read-only mode`);
  }

  if (!toolName || !KNOWN_TOOLS.has(toolName)) {
    return decision('ask', `Unknown tool requires approval: ${toolName || 'unnamed tool'}`);
  }

  let pathInspection;
  if (PATH_TOOLS.has(toolName)) {
    pathInspection = inspectToolPaths(toolName, args, input.cwd);
    if (!pathInspection.ok) {
      return decision('ask', `Unable to verify ${toolName} path: ${pathInspection.reason}`);
    }
    if (pathInspection.sensitive && mode !== 'auto') {
      return decision('ask', `${toolName} targets a sensitive-looking path`);
    }
    if (toolName === 'grep' && mode !== 'auto') {
      return decision('ask', 'Bulk content search requires approval because it may traverse sensitive files');
    }
    if (mode === 'on-risk' && READ_PATH_TOOLS.has(toolName) && pathInspection.paths.every((candidate) => isWithinTrustedReadRoot(candidate, input))) {
      return decision('allow', `${toolName} targets a trusted Pi/ipi runtime path`);
    }
  }

  const rules = input.approvalRules && typeof input.approvalRules === 'object'
    ? input.approvalRules
    : null;
  if (rules) {
    if (READ_PATH_TOOLS.has(toolName)) {
      if (!pathInspection.outside) return decision('allow', `${toolName} is inside the workspace`);
      const action = normalizeRule(rules.read_outside_workspace, 'ask');
      return decision(action, `${toolName} targets a path outside the workspace`);
    }
    if (toolName === 'bash') {
      const action = normalizeRule(rules.bash, 'ask');
      return decision(action, 'Custom bash approval rule');
    }
    if (MUTATING_TOOLS.has(toolName)) {
      const action = normalizeRule(rules[toolName], 'ask');
      return decision(action, `Custom ${toolName} approval rule`);
    }
    return decision('ask', `Tool ${toolName} has no explicit approval rule`);
  }

  if (mode === 'auto') return decision('allow', `Known tool ${toolName} allowed by auto mode`);

  if (READ_PATH_TOOLS.has(toolName)) {
    return pathInspection.outside
      ? decision('ask', `${toolName} targets a path outside the workspace`)
      : decision('allow', `${toolName} is inside the workspace`);
  }

  if (mode === 'on-risk') {
    if (toolName === 'bash') return decision('ask', 'Shell commands require approval in on-risk mode');
    if (MUTATING_TOOLS.has(toolName)) {
      return pathInspection.outside
        ? decision('ask', `${toolName} targets a path outside the workspace`)
        : decision('allow', `${toolName} is inside the workspace`);
    }
  }

  if (toolName === 'bash' || MUTATING_TOOLS.has(toolName)) {
    return decision('ask', `${toolName} requires approval`);
  }

  return decision('ask', `Tool ${toolName} requires approval`);
}
