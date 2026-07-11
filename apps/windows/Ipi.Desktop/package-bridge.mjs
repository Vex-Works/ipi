import fs from 'node:fs';
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

function readJsonFile(file) {
  const raw = fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, '');
  return JSON.parse(raw);
}

function resolvePiCodingAgentRoot(value) {
  if (typeof value !== 'string' || !value.trim()) {
    throw new Error('piCodingAgentRoot is required');
  }
  const root = fs.realpathSync(value.trim());
  const entryPoint = path.join(root, 'dist', 'index.js');
  if (!fs.statSync(entryPoint).isFile()) {
    throw new Error(`Invalid piCodingAgentRoot: ${root}`);
  }
  return { root, entryPoint };
}

async function loadPi(piCodingAgentRoot) {
  const { root, entryPoint } = resolvePiCodingAgentRoot(piCodingAgentRoot);
  const mod = await import(pathToFileURL(entryPoint).href);
  return { root, mod };
}

function emptyCounts() {
  return { extensions: 0, skills: 0, prompts: 0, themes: 0 };
}

function toPluginScope(scope) {
  return scope === 'project' ? 'project' : 'global';
}

function toPiScope(scope) {
  return scope === 'project' ? 'project' : 'user';
}

function localForScope(scope) {
  return scope === 'project';
}

function keyFor(source, scope) {
  return `${scope}\0${source}`;
}

function getPackageSource(entry) {
  return typeof entry === 'string' ? entry : entry?.source || '';
}

function isDisabledPackage(entry) {
  if (typeof entry === 'string') return false;
  return Array.isArray(entry?.extensions) && entry.extensions.length === 0 &&
    Array.isArray(entry?.skills) && entry.skills.length === 0 &&
    Array.isArray(entry?.prompts) && entry.prompts.length === 0 &&
    Array.isArray(entry?.themes) && entry.themes.length === 0;
}

function isWithin(root, candidate) {
  const relative = path.relative(root, candidate);
  return relative === '' || (!relative.startsWith(`..${path.sep}`) && relative !== '..' && !path.isAbsolute(relative));
}

function readUntrustedProjectPackages(cwd, packageManager, diagnostics) {
  const settingsPath = path.join(cwd, '.pi', 'settings.json');
  if (!fs.existsSync(settingsPath)) return [];

  try {
    const workspace = fs.realpathSync(cwd);
    const resolvedSettingsPath = fs.realpathSync(settingsPath);
    if (!isWithin(workspace, resolvedSettingsPath)) {
      throw new Error('project settings resolve outside the workspace');
    }

    const stats = fs.statSync(resolvedSettingsPath);
    if (!stats.isFile()) throw new Error('project settings are not a file');
    if (stats.size > 1024 * 1024) throw new Error('project settings exceed the 1 MiB display limit');

    const settings = readJsonFile(resolvedSettingsPath);
    if (!Array.isArray(settings?.packages)) return [];
    const seen = new Set();
    const packages = [];
    for (const entry of settings.packages) {
      const source = getPackageSource(entry).trim();
      if (!source || seen.has(source)) continue;
      seen.add(source);
      let installedPath;
      try {
        installedPath = packageManager.getInstalledPath(source, 'project');
      } catch (error) {
        diagnostics.push({
          type: 'warning',
          source,
          message: `Unable to resolve the read-only project package path: ${error instanceof Error ? error.message : String(error)}`,
        });
      }
      packages.push({
        source,
        scope: 'project',
        filtered: typeof entry === 'object' && entry !== null,
        disabled: true,
        installedPath,
        packageName: source,
        version: '',
        configuredVersion: getConfiguredVersion(source) || '',
        counts: emptyCounts(),
        resources: [],
        status: 'untrusted',
      });
    }
    if (packages.length > 0) {
      diagnostics.push({
        type: 'warning',
        message: 'Project packages are displayed read-only and are not loaded until workspace trust is implemented.',
      });
    }
    return packages;
  } catch (error) {
    diagnostics.push({
      type: 'error',
      message: `Unable to display untrusted project packages: ${error instanceof Error ? error.message : String(error)}`,
    });
    return [];
  }
}

function getDisabledPackages(settingsManager) {
  const disabled = new Map();
  for (const entry of settingsManager.getGlobalSettings().packages ?? []) {
    disabled.set(keyFor(getPackageSource(entry), 'global'), isDisabledPackage(entry));
  }
  for (const entry of settingsManager.getProjectSettings().packages ?? []) {
    disabled.set(keyFor(getPackageSource(entry), 'project'), isDisabledPackage(entry));
  }
  return disabled;
}

function setPackageDisabled(settingsManager, source, scope, disabled) {
  const current = scope === 'project'
    ? settingsManager.getProjectSettings().packages ?? []
    : settingsManager.getGlobalSettings().packages ?? [];
  let changed = false;
  const next = current.map((entry) => {
    if (getPackageSource(entry) !== source) return entry;
    changed = true;
    if (disabled) {
      return {
        ...(typeof entry === 'string' ? { source: entry } : entry),
        extensions: [],
        skills: [],
        prompts: [],
        themes: [],
      };
    }
    return getPackageSource(entry);
  });
  if (!changed) return false;
  if (scope === 'project') settingsManager.setProjectPackages(next);
  else settingsManager.setPackages(next);
  return true;
}

function getConfiguredVersion(source) {
  const npmSpec = source.startsWith('npm:') ? source.slice(4) : undefined;
  if (npmSpec) {
    const lastAt = npmSpec.lastIndexOf('@');
    const packageNameEnd = npmSpec.startsWith('@') ? npmSpec.indexOf('/', 1) : 0;
    if (lastAt > packageNameEnd) return npmSpec.slice(lastAt + 1) || undefined;
    return undefined;
  }
  if (source.startsWith('git:') || /^[a-z]+:\/\//.test(source)) {
    const lastAt = source.lastIndexOf('@');
    const lastSlash = source.lastIndexOf('/');
    const lastColon = source.lastIndexOf(':');
    if (lastAt > Math.max(lastSlash, lastColon)) return source.slice(lastAt + 1) || undefined;
  }
  return undefined;
}

function readPackageMetadata(installedPath) {
  if (!installedPath) return {};
  try {
    const stats = fs.statSync(installedPath);
    const packageJsonPath = stats.isDirectory()
      ? path.join(installedPath, 'package.json')
      : path.join(path.dirname(installedPath), 'package.json');
    if (!fs.existsSync(packageJsonPath)) return {};
    const parsed = readJsonFile(packageJsonPath);
    return {
      packageName: typeof parsed.name === 'string' ? parsed.name : undefined,
      version: typeof parsed.version === 'string' ? parsed.version : undefined,
    };
  } catch {
    return {};
  }
}

function resourceName(resourcePath, kind) {
  const file = path.basename(resourcePath);
  const ext = path.extname(file);
  if (kind === 'skill' && file.toLowerCase() === 'skill.md') return path.basename(path.dirname(resourcePath));
  if ((kind === 'extension' || kind === 'theme' || kind === 'prompt') && ext) {
    if (kind === 'extension' && /^index\.(ts|js)$/.test(file)) return path.basename(path.dirname(resourcePath));
    return file.slice(0, -ext.length);
  }
  return file;
}

function relativeResourcePath(resource) {
  const baseDir = resource.metadata?.baseDir;
  if (!baseDir) return resource.path;
  const rel = path.relative(baseDir, resource.path);
  return rel && !rel.startsWith('..') ? rel : resource.path;
}

function collectResource(resource, pluralKind, countsByPackage, resourcesByPackage, totals) {
  if (!resource.enabled || resource.metadata?.origin !== 'package') return;
  const source = resource.metadata.source;
  const scope = toPluginScope(resource.metadata.scope);
  const key = keyFor(source, scope);
  const counts = countsByPackage.get(key) ?? emptyCounts();
  counts[pluralKind] += 1;
  totals[pluralKind] += 1;
  countsByPackage.set(key, counts);
  const kind = pluralKind === 'extensions' ? 'extension' : pluralKind === 'skills' ? 'skill' : pluralKind === 'prompts' ? 'prompt' : 'theme';
  const resources = resourcesByPackage.get(key) ?? [];
  resources.push({
    kind,
    name: resourceName(resource.path, kind),
    path: resource.path,
    relativePath: relativeResourcePath(resource),
  });
  resourcesByPackage.set(key, resources);
}

function collectResources(paths) {
  const countsByPackage = new Map();
  const resourcesByPackage = new Map();
  const totals = emptyCounts();
  for (const resource of paths.extensions ?? []) collectResource(resource, 'extensions', countsByPackage, resourcesByPackage, totals);
  for (const resource of paths.skills ?? []) collectResource(resource, 'skills', countsByPackage, resourcesByPackage, totals);
  for (const resource of paths.prompts ?? []) collectResource(resource, 'prompts', countsByPackage, resourcesByPackage, totals);
  for (const resource of paths.themes ?? []) collectResource(resource, 'themes', countsByPackage, resourcesByPackage, totals);
  return { countsByPackage, resourcesByPackage, totals };
}

function createManagers(input, mod) {
  const cwd = input.cwd || process.cwd();
  const agentDir = input.agentDir || mod.getAgentDir?.();
  if (!fs.existsSync(cwd)) throw new Error(`cwd does not exist: ${cwd}`);
  const settingsManager = mod.SettingsManager.create(cwd, agentDir, { projectTrusted: false });
  const packageManager = new mod.DefaultPackageManager({ cwd, agentDir, settingsManager });
  packageManager.setProgressCallback((event) => emit({ type: 'progress', event }));
  return { cwd, agentDir, settingsManager, packageManager };
}

async function readPackages(input, mod) {
  const { cwd, settingsManager, packageManager } = createManagers(input, mod);
  const diagnostics = [];
  let countsByPackage = new Map();
  let resourcesByPackage = new Map();
  let totals = emptyCounts();
  const disabledByPackage = getDisabledPackages(settingsManager);

  try {
    const resolved = await packageManager.resolve(async (source) => {
      diagnostics.push({ type: 'warning', source, message: 'Package is configured but not installed yet.' });
      return 'skip';
    });
    ({ countsByPackage, resourcesByPackage, totals } = collectResources(resolved));
  } catch (error) {
    diagnostics.push({ type: 'error', message: error instanceof Error ? error.message : String(error) });
  }

  const packages = packageManager.listConfiguredPackages().map((pkg) => {
    const scope = toPluginScope(pkg.scope);
    const key = keyFor(pkg.source, scope);
    const disabled = disabledByPackage.get(key) ?? false;
    const counts = countsByPackage.get(key) ?? emptyCounts();
    const resources = resourcesByPackage.get(key) ?? [];
    const resourceCount = counts.extensions + counts.skills + counts.prompts + counts.themes;
    const installedPath = pkg.installedPath || packageManager.getInstalledPath(pkg.source, toPiScope(scope));
    const metadata = readPackageMetadata(installedPath);
    if (!installedPath) diagnostics.push({ type: 'warning', source: pkg.source, message: 'Configured package path was not found.' });
    return {
      source: pkg.source,
      scope,
      filtered: !!pkg.filtered,
      disabled,
      installedPath,
      packageName: metadata.packageName || pkg.source,
      version: metadata.version || '',
      configuredVersion: getConfiguredVersion(pkg.source) || '',
      counts,
      resources,
      status: disabled ? 'disabled' : resourceCount > 0 ? 'loaded' : installedPath ? 'installed' : 'missing',
    };
  });
  packages.push(...readUntrustedProjectPackages(cwd, packageManager, diagnostics));

  return { packages, totals, diagnostics };
}

async function runAction(input, mod) {
  const action = String(input.action || 'list');
  const source = String(input.source || '').trim();
  const scope = toPluginScope(input.scope);
  if (scope === 'project' && ['install', 'update', 'remove', 'enable', 'disable'].includes(action)) {
    throw new Error('Project package actions are disabled until explicit workspace trust is implemented.');
  }
  const { settingsManager, packageManager } = createManagers(input, mod);
  if (action === 'install') {
    if (!source) throw new Error('source required');
    await packageManager.installAndPersist(source, { local: localForScope(scope) });
  } else if (action === 'remove') {
    if (!source) throw new Error('source required');
    await packageManager.removeAndPersist(source, { local: localForScope(scope) });
  } else if (action === 'update') {
    await packageManager.update(source || undefined);
  } else if (action === 'disable' || action === 'enable') {
    if (!source) throw new Error('source required');
    const changed = setPackageDisabled(settingsManager, source, scope, action === 'disable');
    if (changed) await settingsManager.flush();
  } else if (action !== 'list') {
    throw new Error(`Unsupported package action: ${action}`);
  }
  return readPackages(input, mod);
}

async function main() {
  const input = await readInput();
  const { root, mod } = await loadPi(input.piCodingAgentRoot);
  const response = await runAction(input, mod);
  emit({ type: 'packages', packageRoot: root, ...response });
  inputLines.close();
}

main().catch((error) => {
  emit({ type: 'error', message: error instanceof Error ? error.message : String(error) });
  process.exitCode = 1;
  inputLines.close();
});
