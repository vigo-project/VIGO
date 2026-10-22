// Syntax coloring of the code editor: the Monarch grammars of Monaco only tell keywords, strings and numbers apart.
// C# (rewritten) and TypeScript / JavaScript (extended) also get the categories of VS Code's grammars: types,
// functions, variables, members and control keywords. Without a language service they are guessed from the shape of
// the code (PascalCase, a following "(", a preceding "."): close to VS Code, not exact.
// Token names, matched by the theme in editor.js: type, function, variable, property, keyword.control.
import * as monaco from 'monaco-editor/editor/editor.api.js';

/** Generic arguments before a call: spaces only after commas, so that "Task<T> RunAsync<T>(" is not a call of Task. */
const GENERIC_CALL = String.raw`(?=\s*(?:<(?:[\w.?\[\]<>]|,\s*)*>)?\s*\()`;

// ---------------------------------------------------------------- C#

const csharp = {
  defaultToken: '',
  tokenPostfix: '.cs',
  brackets: [
    { open: '{', close: '}', token: 'delimiter.curly' },
    { open: '[', close: ']', token: 'delimiter.square' },
    { open: '(', close: ')', token: 'delimiter.parenthesis' },
    { open: '<', close: '>', token: 'delimiter.angle' },
  ],
  controlKeywords: [
    'if', 'else', 'switch', 'case', 'while', 'do', 'for', 'foreach', 'in', 'break', 'continue', 'goto', 'return',
    'throw', 'try', 'catch', 'finally', 'yield', 'await', 'when',
  ],
  keywords: [
    'extern', 'alias', 'using', 'global', 'bool', 'decimal', 'sbyte', 'byte', 'short', 'ushort', 'int', 'uint', 'long',
    'ulong', 'nint', 'nuint', 'char', 'float', 'double', 'object', 'dynamic', 'string', 'is', 'as', 'ref', 'out', 'this',
    'base', 'new', 'typeof', 'void', 'checked', 'unchecked', 'default', 'delegate', 'var', 'const', 'lock', 'from', 'let',
    'where', 'join', 'on', 'equals', 'into', 'orderby', 'ascending', 'descending', 'select', 'group', 'by', 'namespace',
    'partial', 'class', 'struct', 'interface', 'enum', 'record', 'union', 'extension', 'field', 'event', 'public',
    'protected', 'internal', 'private', 'abstract', 'sealed', 'static', 'readonly', 'volatile', 'virtual',
    'override', 'params', 'get', 'set', 'init', 'add', 'remove', 'required', 'scoped', 'operator', 'true', 'false',
    'implicit', 'explicit', 'null', 'async', 'fixed', 'sizeof', 'stackalloc', 'unsafe', 'nameof', 'with', 'and', 'or',
    'not', 'managed', 'unmanaged', 'notnull',
  ],
  namespaceFollows: ['namespace', 'using'],
  // Keywords followed by a type name: "new Success<T>(…)", "class Foo", "x is Foo", "record struct Foo".
  typeFollows: ['class', 'struct', 'interface', 'enum', 'record', 'union', 'new', 'is', 'as'],
  // Pattern combinators before the type: "x is not Failure { … }".
  patternKeywords: ['not', 'and', 'or'],
  operators: [
    '=', '??', '??=', '||', '&&', '|', '^', '&', '==', '!=', '<=', '>=', '<<', '+', '-', '*', '/', '%', '!', '~', '++',
    '--', '+=', '-=', '*=', '/=', '%=', '&=', '|=', '^=', '<<=', '>>=', '>>', '>>>', '=>',
  ],
  symbols: /[=><!~?:&|+\-*\/\^%]+/,
  escapes: /\\(?:[abfnrtv\\"'0e]|x[0-9A-Fa-f]{1,4}|u[0-9A-Fa-f]{4}|U[0-9A-Fa-f]{8})/,
  tokenizer: {
    root: [
      // Attribute at the start of a line: "[Fact]", "[InlineData(1)]".
      [/^(\s*)(\[)(?=\s*@?[A-Za-z_])/, ['', { token: 'delimiter.square', next: '@attribute' }]],
      // Calls and method declarations: "Run(", "GetAsync<T>(".
      [new RegExp(String.raw`@?[A-Za-z_]\w*` + GENERIC_CALL), {
        cases: {
          '@controlKeywords': 'keyword.control',
          '@keywords': 'keyword',
          '@default': 'function',
        },
      }],
      // Property declarations, initializers and patterns: "Name { get; }", "Name => …", "{ Name = 1 }", "{ Name: … }".
      [/@?[A-Z]\w*(?=\s*(?:\{|=>|=(?![=>])|:(?!:)))/, { cases: { '@keywords': 'keyword', '@default': 'property' } }],
      [/@?[A-Za-z_]\w*/, {
        cases: {
          '@namespaceFollows': { token: 'keyword', next: '@namespace' },
          '@typeFollows': { token: 'keyword', next: '@typeName' },
          '@controlKeywords': 'keyword.control',
          '@keywords': 'keyword',
          '~@?[A-Z]\\w*': 'type',
          '@default': 'variable',
        },
      }],
      { include: '@whitespace' },
      [/\.\.\.?/, 'delimiter'], // spread and range: "[..items]", "a..b"
      // Member access: "x.Name", "Task.Run(…)", "a?.b".
      [/\.(?=\s*@?[A-Za-z_])/, { token: 'delimiter', next: '@member' }],
      [/}/, {
        cases: {
          '$S2==interpolatedstring': { token: 'string.quote', next: '@pop' },
          '$S2==litinterpstring': { token: 'string.quote', next: '@pop' },
          '@default': '@brackets',
        },
      }],
      [/[{}()\[\]]/, '@brackets'],
      [/[<>](?!@symbols)/, '@brackets'],
      [/@symbols/, { cases: { '@operators': 'delimiter', '@default': '' } }],
      [/[0-9][0-9_]*\.[0-9][0-9_]*([eE][\-+]?\d+)?[fFdDmM]?/, 'number.float'],
      [/\.[0-9][0-9_]*([eE][\-+]?\d+)?[fFdDmM]?/, 'number.float'],
      [/0[xX][0-9a-fA-F_]+([uU][lL]?|[lL][uU]?)?/, 'number.hex'],
      [/0[bB][01_]+([uU][lL]?|[lL][uU]?)?/, 'number.hex'],
      [/[0-9][0-9_]*([eE][\-+]?\d+)?([uU][lL]?|[lL][uU]?|[fFdDmM])?/, 'number'],
      [/[;,.]/, 'delimiter'],
      // Strings: raw ("""…""", interpolated or not), verbatim, interpolated, regular.
      [/\$*"""/, { token: 'string.quote', next: '@rawstring' }],
      [/"([^"\\]|\\.)*$/, 'string.invalid'],
      [/"/, { token: 'string.quote', next: '@string' }],
      [/(\$@|@\$)"/, { token: 'string.quote', next: '@litinterpstring' }],
      [/@"/, { token: 'string.quote', next: '@litstring' }],
      [/\$"/, { token: 'string.quote', next: '@interpolatedstring' }],
      [/'[^\\']'/, 'string'],
      [/(')(@escapes)(')/, ['string', 'string.escape', 'string']],
      [/'/, 'string.invalid'],
    ],
    attribute: [
      [/\s+/, ''],
      [/@?[A-Za-z_]\w*(?=\s*:(?!:))/, 'keyword'], // target: "[assembly: …]"
      [/:/, 'delimiter'],
      [/@?[A-Za-z_]\w*(?=\s*\.)/, 'type'],
      [/\./, 'delimiter'],
      [/@?[A-Za-z_]\w*/, 'type', '@pop'],
      ['', '', '@pop'],
    ],
    typeName: [
      [/\s+/, ''],
      [/@?[A-Za-z_]\w*(?=\s*\.)/, { cases: { '@keywords': { token: 'keyword', next: '@pop' }, '@default': 'type' } }],
      [/\./, 'delimiter'],
      [/@?[A-Za-z_]\w*/, {
        cases: {
          '@typeFollows': { token: 'keyword', switchTo: '@typeName' },
          '@patternKeywords': 'keyword',
          '@keywords': { token: 'keyword', next: '@pop' },
          '@default': { token: 'type', next: '@pop' },
        },
      }],
      ['', '', '@pop'],
    ],
    member: [
      [/\s+/, ''],
      [new RegExp(String.raw`@?[A-Za-z_]\w*` + GENERIC_CALL), 'function', '@pop'],
      [/@?[A-Za-z_]\w*/, 'property', '@pop'],
      ['', '', '@pop'],
    ],
    namespace: [
      { include: '@whitespace' },
      [/[A-Z]\w*/, 'namespace'],
      [/[.=]/, 'delimiter'],
      ['', '', '@pop'],
    ],
    comment: [
      [/[^\/*]+/, 'comment'],
      ['\\*/', 'comment', '@pop'],
      [/[\/*]/, 'comment'],
    ],
    string: [
      [/[^\\"]+/, 'string'],
      [/@escapes/, 'string.escape'],
      [/\\./, 'string.escape.invalid'],
      [/"/, { token: 'string.quote', next: '@pop' }],
    ],
    rawstring: [
      [/[^"]+/, 'string'],
      [/"""/, { token: 'string.quote', next: '@pop' }],
      [/"/, 'string'],
    ],
    litstring: [
      [/[^"]+/, 'string'],
      [/""/, 'string.escape'],
      [/"/, { token: 'string.quote', next: '@pop' }],
    ],
    litinterpstring: [
      [/[^"{]+/, 'string'],
      [/""/, 'string.escape'],
      [/{{/, 'string.escape'],
      [/}}/, 'string.escape'],
      [/{/, { token: 'string.quote', next: 'root.litinterpstring' }],
      [/"/, { token: 'string.quote', next: '@pop' }],
    ],
    interpolatedstring: [
      [/[^\\"{]+/, 'string'],
      [/@escapes/, 'string.escape'],
      [/\\./, 'string.escape.invalid'],
      [/{{/, 'string.escape'],
      [/}}/, 'string.escape'],
      [/{/, { token: 'string.quote', next: 'root.interpolatedstring' }],
      [/"/, { token: 'string.quote', next: '@pop' }],
    ],
    whitespace: [
      [/^[ \t\v\f]*#\w.*$/, 'keyword.directive'],
      [/[ \t\v\f\r\n]+/, ''],
      [/\/\*/, 'comment', '@comment'],
      [/\/\/.*$/, 'comment'],
    ],
  },
};

// ---------------------------------------------------------------- TypeScript / JavaScript

/** Adds functions, variables, members and control keywords to the TypeScript or JavaScript grammar of Monaco. */
function script(base) {
  const id = String.raw`#?[a-zA-Z_$][\w$]*`;
  const call = new RegExp(id + GENERIC_CALL);
  return {
    ...base,
    controlKeywords: [
      'if', 'else', 'switch', 'case', 'default', 'while', 'do', 'for', 'in', 'of', 'break', 'continue', 'return',
      'throw', 'try', 'catch', 'finally', 'await', 'yield',
    ],
    tokenizer: {
      ...base.tokenizer,
      common: [
        [/(new)(\s+)([A-Z][\w$]*)/, ['keyword', '', 'type.identifier']],
        [call, { cases: { '@controlKeywords': 'keyword.control', '@keywords': 'keyword', '@default': 'function' } }],
        [/#?[a-z_$][\w$]*/, { cases: { '@controlKeywords': 'keyword.control', '@keywords': 'keyword', '@default': 'variable' } }],
        [/\.\.\./, 'delimiter'],
        [/\.(?=\s*#?[a-zA-Z_$])/, { token: 'delimiter', next: '@member' }],
        ...base.tokenizer.common,
      ],
      member: [
        [/\s+/, ''],
        [call, 'function', '@pop'],
        [new RegExp(id), 'property', '@pop'],
        ['', '', '@pop'],
      ],
    },
  };
}

// ---------------------------------------------------------------- registration

// Replaces the tokens providers registered by Monaco (register.all); its language configurations (brackets,
// comments, folding) are kept. Grammars stay loaded on first use.
monaco.languages.registerTokensProviderFactory('csharp', { create: () => csharp });
monaco.languages.registerTokensProviderFactory('typescript', {
  create: async () => script((await import('monaco-editor/languages/definitions/typescript/typescript.js')).language),
});
monaco.languages.registerTokensProviderFactory('javascript', {
  create: async () => script((await import('monaco-editor/languages/definitions/javascript/javascript.js')).language),
});
