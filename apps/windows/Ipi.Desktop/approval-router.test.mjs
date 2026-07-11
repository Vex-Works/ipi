import assert from 'node:assert/strict';
import test from 'node:test';

import { ApprovalRouter } from './approval-router.mjs';

function createLineQueue() {
  const buffered = [];
  const waiting = [];
  let closed = false;

  return {
    [Symbol.asyncIterator]() {
      return {
        next() {
          if (buffered.length > 0) return Promise.resolve({ value: buffered.shift(), done: false });
          if (closed) return Promise.resolve({ value: undefined, done: true });
          return new Promise((resolve) => waiting.push(resolve));
        },
      };
    },
    push(line) {
      if (closed) throw new Error('queue is closed');
      const resolve = waiting.shift();
      if (resolve) resolve({ value: line, done: false });
      else buffered.push(line);
    },
    close() {
      closed = true;
      for (const resolve of waiting.splice(0)) resolve({ value: undefined, done: true });
    },
  };
}

test('routes concurrent approval responses by id when replies arrive in reverse order', async () => {
  const lines = createLineQueue();
  const router = new ApprovalRouter(lines);
  const first = router.waitFor('first');
  const second = router.waitFor('second');

  lines.push(JSON.stringify({ type: 'approval_response', approvalId: 'second', approved: true, reason: 'second allowed' }));
  lines.push(JSON.stringify({ type: 'approval_response', approvalId: 'first', approved: false, reason: 'first denied' }));

  assert.deepEqual(await second, { approved: true, reason: 'second allowed' });
  assert.deepEqual(await first, { approved: false, reason: 'first denied' });
  lines.close();
  await router.done;
});

test('fails every pending approval closed when input ends', async () => {
  const lines = createLineQueue();
  const router = new ApprovalRouter(lines);
  const first = router.waitFor('first');
  const second = router.waitFor('second');

  lines.close();

  assert.deepEqual(await first, { approved: false, reason: 'approval input closed' });
  assert.deepEqual(await second, { approved: false, reason: 'approval input closed' });
  assert.deepEqual(await router.waitFor('late'), { approved: false, reason: 'approval input closed' });
  await router.done;
});
