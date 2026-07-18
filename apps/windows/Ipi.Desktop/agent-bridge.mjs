import fs from 'node:fs';
import path from 'node:path';
import readline from 'node:readline';
import { pathToFileURL } from 'node:url';

import { ApprovalRouter } from './approval-router.mjs';
import { buildToolApprovalScope, decideToolPolicy } from './bridge-policy.mjs';

function emit(payload) {
  process.stdout.write(`${JSON.stringify(payload)}\n`);
}

const inputLines = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
const inputIterator = inputLines[Symbol.asyncIterator]();

async function readInput() {
  const first = await inputIterator.next();
  return JSON.parse(first.value || '{}');
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

async function requestToolApproval(toolName, args, approvalRouter, scopeInput) {
  const approvalId = `${Date.now()}-${Math.random().toString(16).slice(2)}`;
  const summary = summarizeToolRequest(toolName, args);
  const requestScope = buildToolApprovalScope(toolName, args, scopeInput);
  const decision = approvalRouter.waitFor(approvalId);
  emit({
    type: 'approval_request',
    approvalId,
    toolName,
    requestScope,
    summary,
    detail: JSON.stringify(sanitize(args), null, 2),
  });
  return decision;
}

async function main() {
  const input = await readInput();
  const approvalRouter = new ApprovalRouter(inputIterator);
  const cwd = input.cwd || process.cwd();
  const agentDir = input.agentDir;
  const command = String(input.command || 'prompt');
  const message = String(input.message || '').trim();
  if (command === 'prompt' && !message) throw new Error('message is required');
  if (command === 'compact' && !input.sessionFile) throw new Error('sessionFile is required for compaction');
  if (!fs.existsSync(cwd)) throw new Error(`cwd does not exist: ${cwd}`);

  const { root, mod } = await loadPi(input.piCodingAgentRoot);
  const { createAgentSession, SessionManager, getAgentDir, AuthStorage, ModelRegistry, SettingsManager } = mod;
  const resolvedAgentDir = agentDir || getAgentDir?.();
  if (typeof resolvedAgentDir !== 'string' || !resolvedAgentDir.trim()) throw new Error('agentDir is required');

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

    const supportedThinkingLevels = (model) => {
      if (!model.reasoning) return [];
      return ['minimal', 'low', 'medium', 'high', 'xhigh', 'max'].filter((level) => {
        const mapped = model.thinkingLevelMap?.[level];
        if (mapped === null) return false;
        if (level === 'xhigh' || level === 'max') return mapped !== undefined;
        return true;
      });
    };
    const models = modelRegistry.getAvailable().map((model) => ({
      provider: model.provider,
      model: model.id,
      displayName: model.name || model.id,
      source: 'Pi registry',
      isConfigured: true,
      contextWindow: typeof model.contextWindow === 'number' ? model.contextWindow : undefined,
      thinkingLevels: supportedThinkingLevels(model),
      providerDisplayName: modelRegistry.getProviderDisplayName(model.provider) || model.provider,
    }));
    emit({ type: 'models', packageRoot: root, error: modelRegistry.getError?.() || '', models });
    inputLines.close();
    return;
  }

  if (command === 'oauth_login') {
    const oauthProvider = typeof input.oauthProvider === 'string' ? input.oauthProvider.trim() : '';
    const supportedOAuthProviders = new Set(['anthropic', 'openai-codex', 'github-copilot']);
    if (!supportedOAuthProviders.has(oauthProvider)) {
      throw new Error(`Unsupported Pi subscription provider: ${oauthProvider || '(missing)'}`);
    }
    const authStorage = AuthStorage.create(path.join(resolvedAgentDir, 'auth.json'));
    emit({ type: 'oauth_status', message: 'Preparing subscription sign-in…' });
    await authStorage.login(oauthProvider, {
      onAuth: ({ url }) => emit({ type: 'oauth_auth_url', url }),
      onDeviceCode: ({ userCode, verificationUri }) => emit({ type: 'oauth_status', message: `Open ${verificationUri} and enter code ${userCode}.` }),
      onPrompt: async ({ message }) => { throw new Error(`Subscription sign-in needs browser confirmation: ${message}`); },
      onSelect: async ({ options }) => options?.[0]?.id,
    });
    emit({ type: 'oauth_complete', provider: oauthProvider });
    inputLines.close();
    return;
  }

  const sessionManager = input.sessionFile
    ? SessionManager.open(input.sessionFile, undefined)
    : SessionManager.create(cwd, undefined);
  const settingsManager = SettingsManager.create(cwd, resolvedAgentDir, { projectTrusted: false });

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
    settingsManager,
    ...(selectedModel ? { model: selectedModel } : {}),
    ...(input.thinkingLevel ? { thinkingLevel: input.thinkingLevel } : {}),
    ...(Array.isArray(input.tools) ? { tools: input.tools } : {}),
    ...(input.noTools ? { noTools: input.noTools } : {}),
  });

  try {
    const upstreamBeforeToolCall = session.agent.beforeToolCall;
    session.agent.beforeToolCall = async (context, signal) => {
      const toolName = context?.toolCall?.name || 'tool';
      const args = context?.args ?? {};
      emit({ type: 'event', eventType: 'tool_intent', kind: 'tool', label: toolName, detail: summarizeToolRequest(toolName, args) });
      const scopeInput = { ...input, cwd };
      const policy = decideToolPolicy(toolName, args, scopeInput);
      if (policy.action === 'deny') return { block: true, reason: policy.reason };
      if (policy.action === 'ask') {
        const approval = await requestToolApproval(toolName, args, approvalRouter, scopeInput);
        if (!approval?.approved) return { block: true, reason: approval?.reason || `User denied ${toolName}` };
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
  } finally {
    session.dispose?.();
  }
  inputLines.close();
}

main().catch((error) => {
  emit({ type: 'error', message: error instanceof Error ? error.message : String(error), stack: error?.stack });
  inputLines.close();
  process.exitCode = 1;
});
