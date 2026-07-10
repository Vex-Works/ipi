import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import readline from 'node:readline';
import { pathToFileURL } from 'node:url';

function emit(payload) {
  process.stdout.write(`${JSON.stringify(payload)}\n`);
}

const inputLines = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
const inputIterator = inputLines[Symbol.asyncIterator]();

async function readInput() {
  const first = await inputIterator.next();
  return JSON.parse(first.value || '{}');
}

function ipiAppDataDir() {
  return process.env.IPI_APPDATA_DIR || path.join(process.env.APPDATA || path.join(os.homedir(), 'AppData', 'Roaming'), 'ipi');
}

function ipiLocalAppDataDir() {
  return process.env.IPI_LOCALAPPDATA_DIR || path.join(process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'), 'ipi');
}

function readRuntimeConfig() {
  try {
    const configPath = path.join(ipiAppDataDir(), 'runtime.json');
    if (!fs.existsSync(configPath)) return {};
    return JSON.parse(fs.readFileSync(configPath, 'utf8')) || {};
  } catch {
    return {};
  }
}

function findPiCodingAgentRoot(agentDir) {
  const runtimeConfig = readRuntimeConfig();
  const candidates = [
    process.env.PI_CODING_AGENT_ROOT,
    runtimeConfig.piCodingAgentRoot,
    agentDir ? path.join(agentDir, 'npm', 'node_modules', '@earendil-works', 'pi-coding-agent') : '',
    agentDir ? path.join(agentDir, 'npm', 'node_modules', '@agegr', 'pi-web', 'node_modules', '@earendil-works', 'pi-coding-agent') : '',
    path.resolve(process.cwd(), 'node_modules', '@earendil-works', 'pi-coding-agent'),
    path.resolve(process.cwd(), '..', 'node_modules', '@earendil-works', 'pi-coding-agent'),
    path.resolve(process.cwd(), 'pi-web', 'node_modules', '@earendil-works', 'pi-coding-agent'),
    path.join(ipiLocalAppDataDir(), 'runtime', 'pi', 'node_modules', '@earendil-works', 'pi-coding-agent'),
    path.join(os.homedir(), 'AppData', 'Roaming', 'npm', 'node_modules', '@earendil-works', 'pi-coding-agent'),
    path.join(os.homedir(), 'AppData', 'Roaming', 'npm', 'node_modules', '@agegr', 'pi-web', 'node_modules', '@earendil-works', 'pi-coding-agent'),
    path.join(process.env.APPDATA || '', 'npm', 'node_modules', '@earendil-works', 'pi-coding-agent'),
    path.join(process.env.APPDATA || '', 'npm', 'node_modules', '@agegr', 'pi-web', 'node_modules', '@earendil-works', 'pi-coding-agent'),
  ];
  for (const candidate of candidates) {
    if (!candidate || typeof candidate !== 'string') continue;
    if (fs.existsSync(path.join(candidate, 'dist', 'index.js'))) return candidate;
  }
  throw new Error('Could not find @earendil-works/pi-coding-agent. Install pi-web or set piCodingAgentRoot in %AppData%/ipi/runtime.json.');
}

async function loadPi(agentDir) {
  const root = findPiCodingAgentRoot(agentDir);
  const mod = await import(pathToFileURL(path.join(root, 'dist', 'index.js')).href);
  return { root, mod };
}

function sanitize(value, depth = 0, seen = new WeakSet()) {
  if (value == null) return value;
  if (typeof value === 'string') return value.length > 4000 ? `${value.slice(0, 4000)}…` : value;
  if (typeof value === 'number' || typeof value === 'boolean') return value;
  if (typeof value === 'bigint') return value.toString();
  if (typeof value === 'function' || typeof value === 'symbol') return undefined;
  if (depth > 6) return '[depth]';
  if (typeof value === 'object') {
    if (seen.has(value)) return '[circular]';
    seen.add(value);
    if (Array.isArray(value)) return value.slice(0, 40).map((item) => sanitize(item, depth + 1, seen));
    const out = {};
    for (const [key, val] of Object.entries(value)) {
      if (key.startsWith('_')) continue;
      out[key] = sanitize(val, depth + 1, seen);
    }
    return out;
  }
  return String(value);
}

function textFromContent(content) {
  if (!content) return '';
  if (typeof content === 'string') return content;
  if (!Array.isArray(content)) return '';
  const parts = [];
  for (const block of content) {
    if (!block || typeof block !== 'object') continue;
    if (block.type === 'text' && typeof block.text === 'string') parts.push(block.text);
  }
  return parts.join('\n\n').trim();
}

function lastAssistantText(messages) {
  if (!Array.isArray(messages)) return '';
  for (let i = messages.length - 1; i >= 0; i--) {
    const message = messages[i];
    if (message?.role !== 'assistant') continue;
    const text = textFromContent(message.content);
    if (text) return text;
  }
  return '';
}

function summarizeEvent(event) {
  const type = event?.type || 'event';
  if (type === 'agent_start') return { kind: 'state', label: 'agent started', detail: 'waiting for model' };
  if (type === 'agent_end') return { kind: 'state', label: 'agent finished', detail: event?.willRetry ? 'will retry' : '' };
  if (type === 'tool_execution_start') return null;
  if (type === 'tool_execution_end') return event.errorMessage ? { kind: 'tool', label: event.toolName || 'tool', detail: event.errorMessage } : null;
  if (type === 'compaction_start') return { kind: 'state', label: 'compaction started', detail: event.reason || '' };
  if (type === 'compaction_end') return { kind: 'state', label: 'compaction finished', detail: event.errorMessage || event.reason || '' };
  if (type === 'auto_retry_start') return { kind: 'state', label: 'retrying', detail: event.errorMessage || '' };
  if (type === 'prompt_error') return { kind: 'error', label: 'prompt error', detail: event.errorMessage || event.error || '' };
  if (type === 'extension_error') return { kind: 'error', label: 'extension error', detail: event.error || '' };
  return null;
}

function summarizeToolRequest(toolName, args) {
  const cleanArgs = sanitize(args);
  if (toolName === 'bash' && cleanArgs?.command) return cleanArgs.command;
  if ((toolName === 'read' || toolName === 'write' || toolName === 'edit') && cleanArgs?.path) return cleanArgs.path;
  if (toolName === 'edit' && cleanArgs?.filePath) return cleanArgs.filePath;
  const compact = JSON.stringify(cleanArgs ?? {});
  return compact.length > 180 ? `${compact.slice(0, 180)}…` : compact;
}

function isOutsideWorkspace(candidate, input) {
  if (!candidate) return false;
  const root = path.resolve(input.cwd || process.cwd());
  const resolved = path.resolve(root, String(candidate));
  const rootLower = root.toLowerCase();
  const resolvedLower = resolved.toLowerCase();
  return resolvedLower !== rootLower && !resolvedLower.startsWith(`${rootLower}${path.sep}`);
}

function approvalRuleValue(input, key, fallback = 'allow') {
  const rules = input.approvalRules && typeof input.approvalRules === 'object' ? input.approvalRules : null;
  if (!rules) return fallback;
  const value = String(rules[key] || '').toLowerCase();
  return value === 'ask' ? 'ask' : 'allow';
}

function requiresApproval(toolName, args, input) {
  const rules = input.approvalRules && typeof input.approvalRules === 'object' ? input.approvalRules : null;
  const candidate = args?.path || args?.filePath;

  if (rules) {
    if (toolName === 'read') return isOutsideWorkspace(candidate, input) && approvalRuleValue(input, 'read_outside_workspace', 'ask') === 'ask';
    if (toolName === 'bash') return approvalRuleValue(input, 'bash', 'ask') === 'ask';
    if (toolName === 'edit') return approvalRuleValue(input, 'edit', 'ask') === 'ask';
    if (toolName === 'write') return approvalRuleValue(input, 'write', 'ask') === 'ask';
    return false;
  }

  const mode = input.approvalMode || 'default';
  if (mode === 'auto' || mode === 'read-only') return false;

  if (toolName === 'read') return false;

  if (mode === 'on-risk') {
    if (toolName === 'bash') return true;
    if (['write', 'edit'].includes(toolName)) return isOutsideWorkspace(candidate, input);
    return false;
  }

  if (['write', 'edit', 'bash'].includes(toolName)) return true;
  return false;
}

async function requestToolApproval(toolName, args, input) {
  const approvalId = `${Date.now()}-${Math.random().toString(16).slice(2)}`;
  const summary = summarizeToolRequest(toolName, args);
  emit({
    type: 'approval_request',
    approvalId,
    toolName,
    summary,
    detail: JSON.stringify(sanitize(args), null, 2),
  });

  while (true) {
    const line = await inputIterator.next();
    if (line.done) return { approved: false, reason: 'approval input closed' };
    try {
      const response = JSON.parse(line.value || '{}');
      if (response.type === 'approval_response' && response.approvalId === approvalId) {
        return { approved: !!response.approved, reason: String(response.reason || '') };
      }
    } catch {
      // Ignore malformed control messages.
    }
  }
}

async function main() {
  const input = await readInput();
  const cwd = input.cwd || process.cwd();
  const agentDir = input.agentDir;
  const command = String(input.command || 'prompt');
  const message = String(input.message || '').trim();
  if (command === 'prompt' && !message) throw new Error('message is required');
  if (command === 'compact' && !input.sessionFile) throw new Error('sessionFile is required for compaction');
  if (!fs.existsSync(cwd)) throw new Error(`cwd does not exist: ${cwd}`);

  const { root, mod } = await loadPi(agentDir);
  const { createAgentSession, SessionManager, getAgentDir, AuthStorage, ModelRegistry } = mod;
  const resolvedAgentDir = agentDir || getAgentDir?.();

  if (command === 'models' || command === 'provider_catalog') {
    const authStorage = AuthStorage.create(path.join(resolvedAgentDir, 'auth.json'));
    const modelRegistry = ModelRegistry.create(authStorage, path.join(resolvedAgentDir, 'models.json'));
    if (command === 'provider_catalog') {
      const providers = new Map();
      for (const model of modelRegistry.getAll()) {
        const current = providers.get(model.provider) || {
          provider: model.provider,
          displayName: modelRegistry.getProviderDisplayName(model.provider) || model.provider,
          api: model.api || '',
          baseUrl: model.baseUrl || '',
          modelCount: 0,
          isConfigured: false,
        };
        current.modelCount += 1;
        current.isConfigured = current.isConfigured || modelRegistry.hasConfiguredAuth(model);
        if (!current.baseUrl && model.baseUrl) current.baseUrl = model.baseUrl;
        if (!current.api && model.api) current.api = model.api;
        providers.set(model.provider, current);
      }
      emit({ type: 'provider_catalog', packageRoot: root, error: modelRegistry.getError?.() || '', providers: [...providers.values()] });
      inputLines.close();
      return;
    }

    const models = modelRegistry.getAvailable().map((model) => ({
      provider: model.provider,
      model: model.id,
      displayName: model.name || model.id,
      source: 'Pi registry',
      isConfigured: true,
      contextWindow: typeof model.contextWindow === 'number' ? model.contextWindow : undefined,
      providerDisplayName: modelRegistry.getProviderDisplayName(model.provider) || model.provider,
    }));
    emit({ type: 'models', packageRoot: root, error: modelRegistry.getError?.() || '', models });
    inputLines.close();
    return;
  }

  const sessionManager = input.sessionFile
    ? SessionManager.open(input.sessionFile, undefined)
    : SessionManager.create(cwd, undefined);

  if (typeof input.branchFromEntryId === 'string' && input.sessionFile) {
    const branchFromEntryId = input.branchFromEntryId.trim();
    if (branchFromEntryId) sessionManager.branch(branchFromEntryId);
    else sessionManager.resetLeaf();
  }

  let selectedModel;
  const requestedProvider = typeof input.provider === 'string' ? input.provider.trim() : '';
  const requestedModel = typeof input.model === 'string' ? input.model.trim() : '';
  if (requestedProvider && requestedModel && requestedProvider !== 'unknown' && requestedModel !== 'unknown') {
    try {
      const authStorage = AuthStorage.create(path.join(resolvedAgentDir, 'auth.json'));
      const modelRegistry = ModelRegistry.create(authStorage, path.join(resolvedAgentDir, 'models.json'));
      selectedModel = modelRegistry.find(requestedProvider, requestedModel);
      if (!selectedModel) emit({ type: 'event', eventType: 'model_fallback', kind: 'state', label: 'model fallback', detail: `${requestedProvider}/${requestedModel}` });
    } catch (error) {
      emit({ type: 'event', eventType: 'model_fallback', kind: 'state', label: 'model fallback', detail: error instanceof Error ? error.message : String(error) });
    }
  }

  const { session } = await createAgentSession({
    cwd,
    agentDir: resolvedAgentDir,
    sessionManager,
    ...(selectedModel ? { model: selectedModel } : {}),
    ...(input.thinkingLevel ? { thinkingLevel: input.thinkingLevel } : {}),
    ...(Array.isArray(input.tools) ? { tools: input.tools } : {}),
    ...(input.noTools ? { noTools: input.noTools } : {}),
  });

  const upstreamBeforeToolCall = session.agent.beforeToolCall;
  session.agent.beforeToolCall = async (context, signal) => {
    const toolName = context?.toolCall?.name || 'tool';
    const args = context?.args ?? {};
    emit({ type: 'event', eventType: 'tool_intent', kind: 'tool', label: toolName, detail: summarizeToolRequest(toolName, args) });
    if (requiresApproval(toolName, args, input)) {
      const decision = await requestToolApproval(toolName, args, input);
      if (!decision?.approved) return { block: true, reason: decision?.reason || `User denied ${toolName}` };
    }
    return upstreamBeforeToolCall ? await upstreamBeforeToolCall(context, signal) : undefined;
  };

  let finalText = '';
  session.subscribe((event) => {
    if (event?.type === 'agent_end') finalText = lastAssistantText(event.messages) || finalText;
    const summary = summarizeEvent(event);
    if (summary) emit({ type: 'event', eventType: event.type, ...summary });
  });

  emit({ type: 'ready', sessionId: session.sessionId, sessionFile: session.sessionFile, packageRoot: root });
  if (command === 'compact') {
    const result = await session.compact(input.compactInstructions || undefined);
    const before = typeof result?.tokensBefore === 'number' ? result.tokensBefore : undefined;
    const after = typeof result?.estimatedTokensAfter === 'number' ? result.estimatedTokensAfter : undefined;
    finalText = before && after ? `Compacted context: ${before} → ${after} tokens` : 'Compacted context';
  } else {
    await session.prompt(message, { source: 'rpc' });
  }
  emit({ type: 'done', sessionId: session.sessionId, sessionFile: session.sessionFile, finalText });
  session.dispose?.();
  inputLines.close();
}

main().catch((error) => {
  emit({ type: 'error', message: error instanceof Error ? error.message : String(error), stack: error?.stack });
  inputLines.close();
  process.exitCode = 1;
});
