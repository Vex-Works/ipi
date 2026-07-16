import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';

import { buildToolApprovalScope, decideToolPolicy, inspectToolPaths } from './bridge-policy.mjs';

const root = fs.mkdtempSync(path.join(os.tmpdir(), 'ipi-bridge-policy-'));
const workspace = path.join(root, 'workspace');
const outside = path.join(root, 'outside');
const trustedRuntime = path.join(outside, 'pi-runtime');
fs.mkdirSync(workspace);
fs.mkdirSync(outside);
fs.mkdirSync(trustedRuntime);
fs.writeFileSync(path.join(workspace, 'inside.txt'), 'inside');
fs.writeFileSync(path.join(workspace, '.env'), 'SECRET=sample');
fs.writeFileSync(path.join(outside, 'secret.txt'), 'secret');
fs.writeFileSync(path.join(trustedRuntime, 'SKILL.md'), 'trusted skill');
fs.writeFileSync(path.join(trustedRuntime, '.env'), 'SECRET=sample');

test.after(() => fs.rmSync(root, { recursive: true, force: true }));

function decide(toolName, args, approvalMode = 'default', approvalRules = null) {
  return decideToolPolicy(toolName, args, { cwd: workspace, approvalMode, approvalRules });
}

test('default policy allows in-workspace reads and asks outside', () => {
  assert.equal(decide('read', { path: 'inside.txt' }).action, 'allow');
  assert.equal(decide('read', { path: path.join(outside, 'secret.txt') }).action, 'ask');
});

test('path-based discovery tools enforce workspace boundaries', () => {
  for (const tool of ['find', 'ls']) {
    assert.equal(decide(tool, { path: workspace }).action, 'allow');
    assert.equal(decide(tool, { path: outside }).action, 'ask');
  }
  assert.equal(decide('grep', { path: workspace, pattern: 'text' }).action, 'ask');
  assert.equal(decide('grep', { path: workspace, pattern: 'text' }, 'auto').action, 'allow');
});

test('default asks for shell and mutations', () => {
  assert.equal(decide('bash', { command: 'git status' }).action, 'ask');
  assert.equal(decide('edit', { path: 'inside.txt' }).action, 'ask');
  assert.equal(decide('write', { path: 'new.txt' }).action, 'ask');
});

test('sensitive-looking workspace paths require explicit approval outside full-auto mode', () => {
  const rules = { read_outside_workspace: 'allow', write: 'allow', edit: 'allow' };
  assert.equal(decide('read', { path: '.env' }).action, 'ask');
  assert.equal(decide('read', { path: '.env' }, 'on-risk').action, 'ask');
  assert.equal(decide('read', { path: '.env' }, 'custom', rules).action, 'ask');
  assert.equal(decide('write', { path: '.env' }, 'on-risk').action, 'ask');
  assert.equal(decide('read', { path: '.env' }, 'auto').action, 'allow');
});

test('bash approval scopes hash the complete command beyond the UI summary limit', () => {
  const sharedPrefix = 'x'.repeat(5000);
  const first = buildToolApprovalScope('bash', { command: `${sharedPrefix}:first` }, { cwd: workspace });
  const second = buildToolApprovalScope('bash', { command: `${sharedPrefix}:second` }, { cwd: workspace });
  assert.match(first, /^v1:bash:command:sha256:[a-f0-9]{64}$/);
  assert.notEqual(first, second);
  assert.equal(first, buildToolApprovalScope('bash', { command: `${sharedPrefix}:first` }, { cwd: workspace }));
});

test('write and edit approval scopes bind content as well as canonical path', () => {
  const firstWrite = buildToolApprovalScope('write', { path: 'inside.txt', content: 'safe' }, { cwd: workspace });
  const secondWrite = buildToolApprovalScope('write', { path: 'inside.txt', content: 'destructive' }, { cwd: workspace });
  const firstEdit = buildToolApprovalScope('edit', { path: 'inside.txt', oldText: 'a', newText: 'b' }, { cwd: workspace });
  const secondEdit = buildToolApprovalScope('edit', { path: 'inside.txt', oldText: 'a', newText: 'c' }, { cwd: workspace });
  assert.match(firstWrite, /^v1:write:args:sha256:[a-f0-9]{64}$/);
  assert.match(firstEdit, /^v1:edit:args:sha256:[a-f0-9]{64}$/);
  assert.notEqual(firstWrite, secondWrite);
  assert.notEqual(firstEdit, secondEdit);
});

test('search approval scopes bind the query while canonicalizing its path', () => {
  const first = buildToolApprovalScope('grep', { path: workspace, pattern: 'public' }, { cwd: workspace });
  const second = buildToolApprovalScope('grep', { path: workspace, pattern: 'secret' }, { cwd: workspace });
  assert.match(first, /^v1:grep:args:sha256:[a-f0-9]{64}$/);
  assert.notEqual(first, second);
});

test('read-only hard denies shell and mutations', () => {
  assert.equal(decide('bash', { command: 'git status' }, 'read-only').action, 'deny');
  assert.equal(decide('edit', { path: 'inside.txt' }, 'read-only').action, 'deny');
  assert.equal(decide('write', { path: 'new.txt' }, 'read-only').action, 'deny');
  assert.equal(decide('extension_tool', {}, 'read-only').action, 'deny');
  assert.equal(decide('', {}, 'read-only').action, 'deny');
  assert.equal(decide('read', { path: 'inside.txt' }, 'read-only').action, 'allow');
  assert.equal(decide('read', { path: path.join(outside, 'secret.txt') }, 'read-only').action, 'ask');
});

test('on-risk only auto-allows in-workspace mutations', () => {
  assert.equal(decide('edit', { path: 'inside.txt' }, 'on-risk').action, 'allow');
  assert.equal(decide('write', { path: 'new.txt' }, 'on-risk').action, 'allow');
  assert.equal(decide('write', { path: path.join(outside, 'new.txt') }, 'on-risk').action, 'ask');
  assert.equal(decide('bash', { command: 'git status' }, 'on-risk').action, 'ask');
});

test('on-risk allows non-sensitive reads from declared Pi runtime roots', () => {
  const input = { cwd: workspace, approvalMode: 'on-risk', trustedReadRoots: [trustedRuntime] };
  assert.equal(decideToolPolicy('read', { path: path.join(trustedRuntime, 'SKILL.md') }, input).action, 'allow');
  assert.equal(decideToolPolicy('ls', { path: trustedRuntime }, input).action, 'allow');
  assert.equal(decideToolPolicy('grep', { path: trustedRuntime, pattern: 'skill' }, input).action, 'ask');
  assert.equal(decideToolPolicy('read', { path: path.join(trustedRuntime, '.env') }, input).action, 'ask');
  assert.equal(decideToolPolicy('read', { path: path.join(outside, 'secret.txt') }, input).action, 'ask');
});

test('auto allows known verified tools but unknown tools still ask', () => {
  assert.equal(decide('write', { path: 'new.txt' }, 'auto').action, 'allow');
  assert.equal(decide('delete_database', {}, 'auto').action, 'ask');
});

test('custom rules cannot make unknown or unverifiable tools fail open', () => {
  const rules = { bash: 'allow', edit: 'allow', write: 'allow', read_outside_workspace: 'allow' };
  assert.equal(decide('bash', { command: 'git status' }, 'custom', rules).action, 'allow');
  assert.equal(decide('read', { path: path.join(outside, 'secret.txt') }, 'custom', rules).action, 'allow');
  assert.equal(decide('read', {}, 'custom', rules).action, 'ask');
  assert.equal(decide('extension_tool', {}, 'custom', rules).action, 'ask');
});

test('missing and invalid paths fail closed', () => {
  assert.equal(decide('read', {}).action, 'ask');
  assert.equal(decide('read', { path: 'missing.txt' }).action, 'ask');
  assert.equal(decide('write', { path: '\0bad' }, 'on-risk').action, 'ask');
  assert.equal(decideToolPolicy('read', { path: 'inside.txt' }, { cwd: path.join(root, 'missing') }).action, 'ask');
});

test('nonexistent write targets are canonicalized through their nearest existing parent', () => {
  const inspection = inspectToolPaths('write', { path: path.join('new-dir', 'file.txt') }, workspace);
  assert.equal(inspection.ok, true);
  assert.equal(inspection.outside, false);
});

test('junctions and symlinks cannot disguise an outside-workspace target', (t) => {
  const link = path.join(workspace, 'outside-link');
  try {
    fs.symlinkSync(outside, link, process.platform === 'win32' ? 'junction' : 'dir');
  } catch (error) {
    t.skip(`symbolic link creation is unavailable: ${error.message}`);
    return;
  }
  assert.equal(decide('read', { path: path.join(link, 'secret.txt') }).action, 'ask');
  assert.equal(decide('write', { path: path.join(link, 'new.txt') }, 'on-risk').action, 'ask');
  const linkedScope = buildToolApprovalScope('read', { path: path.join(link, 'secret.txt') }, { cwd: workspace });
  const canonicalScope = buildToolApprovalScope('read', { path: path.join(outside, 'secret.txt') }, { cwd: workspace });
  assert.match(linkedScope, /^v1:read:paths:sha256:[a-f0-9]{64}$/);
  assert.equal(linkedScope, canonicalScope);
});
