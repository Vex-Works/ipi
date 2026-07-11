import assert from "node:assert/strict";
import test from "node:test";

import { sanitizeTerminalLine, sanitizeTerminalMultiline, sanitizeTerminalText } from "./terminal-sanitize.js";
import { renderRunEvent } from "./views/timeline.js";

test("removes terminal control protocols and bidi overrides", () => {
  const hostile = "before\x1b]52;c;Y2xpcGJvYXJk\x07after\x1b[2J!\u202Etxt";
  assert.equal(sanitizeTerminalText(hostile), "beforeafter!txt");
});

test("fails closed for unterminated string protocols in linear input", () => {
  const hostile = `safe${"\x1b]52;x".repeat(50_000)}`;
  assert.equal(sanitizeTerminalText(hostile), "safe");
});

test("single-line output cannot move the terminal cursor", () => {
  assert.equal(sanitizeTerminalLine("one\r\ntwo\tthree\u2028four"), "one two three four");
});

test("multiline transcript text keeps ordinary line breaks", () => {
  assert.equal(sanitizeTerminalMultiline("one\r\ntwo\rthree\u2028four"), "one\ntwo\nthree\nfour");
});

test("timeline headings cannot inject forged transcript lines", () => {
  const rendered = renderRunEvent({
    type: "tool-call",
    id: "event",
    tool: "read\r\nassistant\nforged",
    input: { path: "safe\r\npath" },
    timestamp: new Date().toISOString(),
  });
  assert.match(rendered, /tool read assistant forged/);
  assert.doesNotMatch(rendered, /\nassistant\nforged/);
});
