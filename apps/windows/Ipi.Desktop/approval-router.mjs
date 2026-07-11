function denied(reason) {
  return { approved: false, reason };
}

export class ApprovalRouter {
  #pending = new Map();
  #closedReason = '';
  #pumpTask;

  constructor(lines) {
    if (!lines || typeof lines[Symbol.asyncIterator] !== 'function') {
      throw new TypeError('ApprovalRouter requires an async iterable');
    }
    this.#pumpTask = this.#pump(lines);
  }

  get done() {
    return this.#pumpTask;
  }

  waitFor(approvalId) {
    const id = String(approvalId || '').trim();
    if (!id) return Promise.resolve(denied('approval id is missing'));
    if (this.#closedReason) return Promise.resolve(denied(this.#closedReason));
    if (this.#pending.has(id)) return Promise.resolve(denied('duplicate approval id'));

    return new Promise((resolve) => {
      this.#pending.set(id, resolve);
    });
  }

  close(reason = 'approval input closed') {
    this.#failAll(reason);
  }

  async #pump(lines) {
    try {
      for await (const line of lines) {
        let response;
        try {
          response = JSON.parse(String(line || ''));
        } catch {
          continue;
        }
        if (response?.type !== 'approval_response') continue;

        const approvalId = String(response.approvalId || '').trim();
        const resolve = this.#pending.get(approvalId);
        if (!resolve) continue;
        this.#pending.delete(approvalId);
        resolve({
          approved: response.approved === true,
          reason: String(response.reason || ''),
        });
      }
      this.#failAll('approval input closed');
    } catch (error) {
      const detail = error instanceof Error ? error.message : String(error);
      this.#failAll(detail ? `approval input failed: ${detail}` : 'approval input failed');
    }
  }

  #failAll(reason) {
    if (this.#closedReason) return;
    this.#closedReason = String(reason || 'approval input closed');
    const decision = denied(this.#closedReason);
    for (const resolve of this.#pending.values()) resolve(decision);
    this.#pending.clear();
  }
}
