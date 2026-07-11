// Terminal text is an execution surface: control protocols can rewrite the
// title, forge links, move the cursor, or modify the clipboard. Parse them in
// one pass so hostile unterminated sequences cannot trigger regex backtracking.

type ParserState = "text" | "escape" | "csi" | "osc" | "osc-escape" | "string" | "string-escape";

function isBidiControl(code: number): boolean {
  return code === 0x061c || code === 0x200e || code === 0x200f ||
    (code >= 0x202a && code <= 0x202e) ||
    (code >= 0x2066 && code <= 0x2069);
}

export function sanitizeTerminalText(value: unknown): string {
  const input = value == null ? "" : String(value);
  let output = "";
  let state: ParserState = "text";

  for (let index = 0; index < input.length; index += 1) {
    const character = input[index]!;
    const code = input.charCodeAt(index);

    if (state === "escape") {
      if (character === "[") state = "csi";
      else if (character === "]") state = "osc";
      else if (character === "P" || character === "X" || character === "^" || character === "_") state = "string";
      else state = character === "\x1b" ? "escape" : "text";
      continue;
    }

    if (state === "csi") {
      if (character === "\x1b") state = "escape";
      else if (code >= 0x40 && code <= 0x7e) state = "text";
      continue;
    }

    if (state === "osc") {
      if (character === "\x07" || code === 0x9c) state = "text";
      else if (character === "\x1b") state = "osc-escape";
      continue;
    }

    if (state === "osc-escape") {
      if (character === "\\") state = "text";
      else state = character === "\x1b" ? "osc-escape" : "osc";
      continue;
    }

    if (state === "string") {
      if (code === 0x9c) state = "text";
      else if (character === "\x1b") state = "string-escape";
      continue;
    }

    if (state === "string-escape") {
      if (character === "\\") state = "text";
      else state = character === "\x1b" ? "string-escape" : "string";
      continue;
    }

    if (character === "\x1b") {
      state = "escape";
      continue;
    }
    if (code === 0x9b) {
      state = "csi";
      continue;
    }
    if (code === 0x9d) {
      state = "osc";
      continue;
    }
    if (code === 0x90 || code === 0x98 || code === 0x9e || code === 0x9f) {
      state = "string";
      continue;
    }
    if (code < 0x20 && code !== 0x09 && code !== 0x0a && code !== 0x0d) continue;
    if ((code >= 0x7f && code <= 0x9f) || isBidiControl(code)) continue;
    output += character;
  }

  return output;
}

// Full-screen rows must never contain line movement characters. Keep the
// multiline sanitizer separate because the plain timeline intentionally uses
// newlines for readable transcript output.
export function sanitizeTerminalLine(value: unknown): string {
  return sanitizeTerminalMultiline(value).replace(/[\t\n]+/g, " ");
}

export function sanitizeTerminalMultiline(value: unknown): string {
  return sanitizeTerminalText(value)
    .replace(/\r\n?/g, "\n")
    .replace(/[\u2028\u2029]/g, "\n");
}
