using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace GhidraLens;

public enum CodeLang { Plain, C, Asm, Json, Pcode }

public static class Highlighter
{
    private const int MaxHighlightChars = 400_000;

    private static SolidColorBrush B(byte r, byte g, byte b) => new(Color.FromArgb(255, r, g, b));
    private static readonly SolidColorBrush Keyword = B(0x56, 0x9C, 0xD6);
    private static readonly SolidColorBrush Control = B(0xC5, 0x86, 0xC0);
    private static readonly SolidColorBrush Type = B(0x4E, 0xC9, 0xB0);
    private static readonly SolidColorBrush Function = B(0xDC, 0xDC, 0xAA);
    private static readonly SolidColorBrush Str = B(0xCE, 0x91, 0x78);
    private static readonly SolidColorBrush Number = B(0xB5, 0xCE, 0xA8);
    private static readonly SolidColorBrush Comment = B(0x6A, 0x99, 0x55);
    private static readonly SolidColorBrush Variable = B(0x9C, 0xDC, 0xFE);
    private static readonly SolidColorBrush Link = B(0x4F, 0xC1, 0xFF);
    private static readonly SolidColorBrush Data = B(0xD7, 0xBA, 0x7D);
    private static readonly SolidColorBrush Punct = B(0x80, 0x80, 0x80);

    private static readonly HashSet<string> CControl = ["if", "else", "while", "for", "do", "return", "switch", "case", "break", "continue", "goto", "default"];
    private static readonly HashSet<string> CKeywords = ["sizeof", "struct", "union", "enum", "typedef", "const", "volatile", "static", "extern", "unsigned", "signed", "__stdcall", "__cdecl", "__fastcall", "__thiscall", "true", "false", "NULL"];
    private static readonly Regex CTypeRx = new(@"^(void|bool|char|uchar|short|ushort|int|uint|long|ulong|longlong|ulonglong|float|double|byte|word|dword|qword|code|string|wchar_t|wchar16|size_t|undefined\d*|u?int\d+_t|BOOL|BYTE|WORD|DWORD|QWORD|HANDLE|LPVOID|LPCSTR|LPCWSTR|LPSTR|LPWSTR|HRESULT|HWND|HMODULE|UINT|ULONG|LONG|SIZE_T|[A-Z][A-Za-z0-9]*_t)$", RegexOptions.Compiled);
    private static readonly Regex AddrSymbolRx = new(@"^(FUN|LAB|DAT|PTR|SUB|thunk_FUN|switchD|caseD|s|u)_([0-9a-fA-F]{6,16})", RegexOptions.Compiled);

    private static readonly Regex CToken = new(
        @"(?<comment>//[^\n]*|/\*[\s\S]*?\*/)|(?<str>""(?:\\.|[^""\\\n])*""|'(?:\\.|[^'\\\n])+')|(?<num>\b0x[0-9a-fA-F]+\b|\b\d+(?:\.\d+)?[uUlLfF]*\b)|(?<id>\b[A-Za-z_][A-Za-z0-9_]*\b)",
        RegexOptions.Compiled);

    private static readonly HashSet<string> Registers = new(StringComparer.OrdinalIgnoreCase)
    {
        "EAX","EBX","ECX","EDX","ESI","EDI","EBP","ESP","EIP","AX","BX","CX","DX","SI","DI","BP","SP","AL","AH","BL","BH","CL","CH","DL","DH",
        "RAX","RBX","RCX","RDX","RSI","RDI","RBP","RSP","RIP","R8","R9","R10","R11","R12","R13","R14","R15",
        "CS","DS","ES","FS","GS","SS","ST0","ST1","XMM0","XMM1","XMM2","XMM3","XMM4","XMM5","XMM6","XMM7",
    };
    private static readonly HashSet<string> AsmWords = new(StringComparer.OrdinalIgnoreCase) { "byte", "word", "dword", "qword", "ptr", "xmmword", "tword" };

    private static readonly Regex AsmToken = new(
        @"(?<addr>^\s*[0-9a-fA-F]{6,16}\b)|(?<num>\b0x[0-9a-fA-F]+\b|-?\b\d+\b)|(?<id>\b[A-Za-z_.][A-Za-z0-9_.]*\b)|(?<punct>[\[\]\+\-\*,:])",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex JsonToken = new(
        @"(?<key>""(?:\\.|[^""\\])*""(?=\s*:))|(?<str>""(?:\\.|[^""\\])*"")|(?<num>-?\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?\b)|(?<kw>\btrue\b|\bfalse\b|\bnull\b)|(?<punct>[{}\[\],:])",
        RegexOptions.Compiled);
    private static readonly Regex HexAddrValue = new(@"^""(?:0x)?([0-9a-fA-F]{8,16})""$", RegexOptions.Compiled);

    private static readonly Regex PcodeToken = new(
        @"(?<addr>^\s*[0-9a-fA-F]{6,16}\b)|(?<block>\[[0-9a-fA-F]+\])|(?<op>\b[A-Z][A-Z0-9_]{2,}\b)|(?<num>0x[0-9a-fA-F]+)|(?<space>\b(?:reg|stack|ram|unique|const)\b)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public static CodeLang Detect(string text)
    {
        var t = text.TrimStart();
        if (t.StartsWith('{') || t.StartsWith('['))
        {
            try { using var _ = JsonDocument.Parse(text); return CodeLang.Json; } catch (JsonException) { }
        }
        if (Regex.IsMatch(t, @"^[0-9a-fA-F]{6,16}\s{2,}\S")) return CodeLang.Asm;
        if (text.Contains(';') && (text.Contains('{') || text.Contains("("))) return CodeLang.C;
        return CodeLang.Plain;
    }

    private static readonly Regex JsonBlock = new(@"^[\{\[][\s\S]*?^[\}\]][ \t]*$", RegexOptions.Compiled | RegexOptions.Multiline);

    public static void SetCode(this RichTextBlock box, string text, CodeLang? lang = null)
    {
        box.Blocks.Clear();
        var p = new Paragraph();
        if (text.Length > MaxHighlightChars) Fill(p.Inlines, text, CodeLang.Plain);
        else if (lang is { } l) Fill(p.Inlines, text, l);
        else FillMixed(p.Inlines, text);
        box.Blocks.Add(p);
    }

    // Output often mixes plain headers with pretty-printed JSON blocks that open and close at column 0.
    private static void FillMixed(InlineCollection inlines, string text)
    {
        var whole = Detect(text);
        if (whole != CodeLang.Plain) { Fill(inlines, text, whole); return; }
        var pos = 0;
        foreach (Match m in JsonBlock.Matches(text))
        {
            if (m.Index > pos) Fill(inlines, text[pos..m.Index], CodeLang.Plain);
            Fill(inlines, m.Value, Detect(m.Value) == CodeLang.Json ? CodeLang.Json : CodeLang.Plain);
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) Fill(inlines, text[pos..], CodeLang.Plain);
    }

    public static void Fill(InlineCollection inlines, string text, CodeLang lang)
    {
        switch (lang)
        {
            case CodeLang.C: Tokenize(inlines, text, CToken, ClassifyC); break;
            case CodeLang.Asm: Tokenize(inlines, text, AsmToken, ClassifyAsm); break;
            case CodeLang.Json: Tokenize(inlines, text, JsonToken, ClassifyJson); break;
            case CodeLang.Pcode: Tokenize(inlines, text, PcodeToken, ClassifyPcode); break;
            default: inlines.Add(new Run { Text = text }); break;
        }
    }

    private delegate Inline? Classifier(Match m, string text);

    private static void Tokenize(InlineCollection inlines, string text, Regex rx, Classifier classify)
    {
        var pos = 0;
        foreach (Match m in rx.Matches(text))
        {
            if (m.Index > pos) inlines.Add(new Run { Text = text[pos..m.Index] });
            inlines.Add(classify(m, text) ?? new Run { Text = m.Value });
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) inlines.Add(new Run { Text = text[pos..] });
    }

    private static Run Colored(string s, Brush brush) => new() { Text = s, Foreground = brush };

    private static Inline AddressLink(string display, string address)
    {
        var h = new Hyperlink { Foreground = Link, UnderlineStyle = UnderlineStyle.None };
        h.Inlines.Add(new Run { Text = display });
        h.Click += (_, _) => MainWindow.Instance?.ShowCodeBrowser(address);
        ToolTipService.SetToolTip(h, $"Go to {address}");
        return h;
    }

    private static Inline? ClassifyC(Match m, string text)
    {
        if (m.Groups["comment"].Success) return Colored(m.Value, Comment);
        if (m.Groups["str"].Success) return Colored(m.Value, Str);
        if (m.Groups["num"].Success) return Colored(m.Value, Number);
        var id = m.Value;
        if (CControl.Contains(id)) return Colored(id, Control);
        if (CKeywords.Contains(id)) return Colored(id, Keyword);
        if (CTypeRx.IsMatch(id)) return Colored(id, Type);
        if (AddrSymbolRx.Match(id) is { Success: true } a)
        {
            var link = AddressLink(id, a.Groups[2].Value);
            if (link is Hyperlink h && !id.StartsWith("FUN") && !id.StartsWith("thunk")) h.Foreground = Data;
            else if (link is Hyperlink f) f.Foreground = Function;
            return link;
        }
        var next = m.Index + m.Length;
        while (next < text.Length && text[next] == ' ') next++;
        if (next < text.Length && text[next] == '(') return Colored(id, Function);
        if (id.StartsWith("param_") || id.StartsWith("local_") || id.StartsWith("uVar") || id.StartsWith("iVar") || id.StartsWith("pv") || id.StartsWith("p") && id.Length > 1 && char.IsUpper(id[1]))
            return Colored(id, Variable);
        return null;
    }

    private static Inline? ClassifyAsm(Match m, string text)
    {
        if (m.Groups["addr"].Success)
        {
            var lead = m.Value.Length - m.Value.TrimStart().Length;
            var span = new Span();
            if (lead > 0) span.Inlines.Add(new Run { Text = m.Value[..lead] });
            span.Inlines.Add(AddressLink(m.Value.Trim(), m.Value.Trim()));
            return span;
        }
        if (m.Groups["num"].Success)
        {
            var v = m.Value;
            return v.StartsWith("0x") && v.Length >= 8 ? AddressLink(v, v[2..]) : Colored(v, Number);
        }
        if (m.Groups["punct"].Success) return Colored(m.Value, Punct);
        var id = m.Value;
        if (Registers.Contains(id)) return Colored(id, Variable);
        if (AsmWords.Contains(id)) return Colored(id, Keyword);
        var lineStart = text.LastIndexOf('\n', Math.Max(0, m.Index - 1)) + 1;
        var before = text[lineStart..m.Index];
        if (Regex.IsMatch(before, @"^\s*[0-9a-fA-F]{6,16}\s+$") || before.Trim().Length == 0)
            return Colored(id, id.StartsWith('J') || id is "CALL" or "RET" or "RETN" ? Control : Keyword);
        if (AddrSymbolRx.Match(id) is { Success: true } a) return AddressLink(id, a.Groups[2].Value);
        return Colored(id, Function);
    }

    private static Inline? ClassifyJson(Match m, string text)
    {
        if (m.Groups["key"].Success) return Colored(m.Value, Variable);
        if (m.Groups["str"].Success)
        {
            if (HexAddrValue.Match(m.Value) is { Success: true } a) return AddressLink(m.Value, a.Groups[1].Value);
            return Colored(m.Value, Str);
        }
        if (m.Groups["num"].Success) return Colored(m.Value, Number);
        if (m.Groups["kw"].Success) return Colored(m.Value, Keyword);
        return Colored(m.Value, Punct);
    }

    private static Inline? ClassifyPcode(Match m, string text)
    {
        if (m.Groups["addr"].Success) return Colored(m.Value, Link);
        if (m.Groups["block"].Success) return Colored(m.Value, Punct);
        if (m.Groups["op"].Success) return Colored(m.Value, m.Value.Contains("BRANCH") || m.Value is "CALL" or "CALLIND" or "RETURN" or "CBRANCH" ? Control : Keyword);
        if (m.Groups["num"].Success) return Colored(m.Value, Number);
        if (m.Groups["space"].Success) return Colored(m.Value, Type);
        return null;
    }

    public static readonly DependencyProperty AsmProperty = DependencyProperty.RegisterAttached(
        "Asm", typeof(string), typeof(Highlighter), new PropertyMetadata(null, (d, e) => Apply(d, e.NewValue as string, CodeLang.Asm)));
    public static string GetAsm(DependencyObject o) => (string)o.GetValue(AsmProperty);
    public static void SetAsm(DependencyObject o, string v) => o.SetValue(AsmProperty, v);

    public static readonly DependencyProperty PcodeProperty = DependencyProperty.RegisterAttached(
        "Pcode", typeof(string), typeof(Highlighter), new PropertyMetadata(null, (d, e) => Apply(d, e.NewValue as string, CodeLang.Pcode)));
    public static string GetPcode(DependencyObject o) => (string)o.GetValue(PcodeProperty);
    public static void SetPcode(DependencyObject o, string v) => o.SetValue(PcodeProperty, v);

    private static void Apply(DependencyObject d, string? text, CodeLang lang)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        if (text is null) return;
        var tmp = new Paragraph();
        Fill(tmp.Inlines, text, lang);
        foreach (var inline in tmp.Inlines.ToList())
        {
            tmp.Inlines.Remove(inline);
            tb.Inlines.Add(inline is Hyperlink h ? Colored(((Run)h.Inlines[0]).Text, h.Foreground) : inline is Span s ? Flatten(s) : inline);
        }
    }

    private static Inline Flatten(Span s)
    {
        var text = string.Concat(s.Inlines.Select(i => i is Run r ? r.Text : i is Hyperlink h ? ((Run)h.Inlines[0]).Text : ""));
        return Colored(text, Link);
    }
}
