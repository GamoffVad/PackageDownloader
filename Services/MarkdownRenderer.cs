using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using Markdig;
using Md  = Markdig.Syntax;
using Mdi = Markdig.Syntax.Inlines;
using Mdt = Markdig.Extensions.Tables;

namespace PackageDownloader.Services;

/// <summary>
/// Преобразует Markdown (через парсер Markdig) в WPF FlowDocument с оформлением
/// под текущую тему: заголовки, списки, таблицы, цитаты, ссылки, инлайн-код и
/// блоки кода с подсветкой ключевых слов/строк/комментариев.
/// </summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    public static FlowDocument Render(string markdown) => new Impl().Build(markdown ?? "");

    private sealed class Impl
    {
        // ── Палитра под тему ────────────────────────────────────────────────
        private readonly bool   _light = Theme.IsLight;
        private readonly Brush  _text, _muted, _border, _accent;
        private readonly Brush  _codeBg, _inlineCodeBg;
        private readonly Brush  _kw, _str, _comment, _num, _type, _codeFg;
        private readonly Brush  _headerBg;

        public Impl()
        {
            _text   = Find("TextBrush",   _light ? "#1B1B2E" : "#E2E2F0");
            _muted  = Find("MutedBrush",  _light ? "#6C6C82" : "#7878A0");
            _border = Find("BorderBrush", _light ? "#DCE0EB" : "#2E2E50");
            _accent = Find("AccentBrush", "#7C3AED");

            _codeBg       = Hex(_light ? "#F1F2F7" : "#15152A");
            _inlineCodeBg = Hex(_light ? "#E7E9F1" : "#26264A");
            _headerBg     = Hex(_light ? "#EDEFF6" : "#20203C");
            _codeFg       = Hex(_light ? "#22223A" : "#D5D5EC");

            _kw      = Hex(_light ? "#7C3AED" : "#B79CFF");
            _str     = Hex(_light ? "#1A7F37" : "#7FD49B");
            _comment = Hex(_light ? "#8A8A9E" : "#6E6E92");
            _num     = Hex(_light ? "#B25000" : "#E2A36A");
            _type    = Hex(_light ? "#0B6EA8" : "#6FD0E0");
        }

        public FlowDocument Build(string markdown)
        {
            var doc = new FlowDocument
            {
                FontFamily   = new FontFamily("Segoe UI"),
                FontSize     = 13.5,
                Foreground   = _text,
                Background    = Brushes.Transparent,
                PagePadding  = new Thickness(0),
                ColumnWidth  = double.PositiveInfinity,
                TextAlignment = TextAlignment.Left
            };

            var parsed = Markdown.Parse(markdown, Pipeline);
            foreach (var block in parsed)
                if (RenderBlock(block) is { } b) doc.Blocks.Add(b);

            return doc;
        }

        // ── Блоки ───────────────────────────────────────────────────────────

        private Block? RenderBlock(Md.Block block) => block switch
        {
            Md.HeadingBlock h        => Heading(h),
            Md.ParagraphBlock p      => Para(p),
            Md.ListBlock l           => BuildList(l),
            Md.QuoteBlock q          => Quote(q),
            Md.FencedCodeBlock c     => CodeBlock(c.Lines.ToString(), c.Info ?? ""),
            Md.CodeBlock c           => CodeBlock(c.Lines.ToString(), ""),
            Md.ThematicBreakBlock    => Rule(),
            Mdt.Table t              => BuildTable(t),
            Md.HtmlBlock html        => HtmlPara(html.Lines.ToString()),
            _                        => Fallback(block)
        };

        private Block HtmlPara(string html)
        {
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 9), LineHeight = 20 };
            foreach (var inline in HtmlToInlines(html)) p.Inlines.Add(inline);
            return p;
        }

        private Block Heading(Md.HeadingBlock h)
        {
            double size = h.Level switch { 1 => 22, 2 => 18.5, 3 => 16, 4 => 14.5, _ => 13 };
            var p = new Paragraph
            {
                FontSize   = size,
                FontWeight = FontWeights.Bold,
                Foreground = h.Level <= 2 ? _text : _muted,
                Margin     = new Thickness(0, h.Level <= 2 ? 16 : 12, 0, 6)
            };
            AddInlines(p.Inlines, h.Inline);
            if (h.Level <= 2)
            {
                p.BorderBrush     = _border;
                p.BorderThickness = new Thickness(0, 0, 0, 1);
                p.Padding         = new Thickness(0, 0, 0, 5);
            }
            return p;
        }

        private Block Para(Md.ParagraphBlock p)
        {
            var par = new Paragraph { Margin = new Thickness(0, 0, 0, 9), LineHeight = 20 };
            AddInlines(par.Inlines, p.Inline);
            return par;
        }

        private Block BuildList(Md.ListBlock list)
        {
            var wpf = new List
            {
                MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                Margin      = new Thickness(0, 0, 0, 9),
                Padding     = new Thickness(22, 0, 0, 0)
            };
            if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start)) wpf.StartIndex = start;

            foreach (var item in list)
            {
                var li = new ListItem();
                if (item is Md.ListItemBlock itemBlock)
                    foreach (var child in itemBlock)
                        if (RenderBlock(child) is { } b) li.Blocks.Add(b);
                // Внутри пунктов абзацы не должны иметь большой нижний отступ.
                foreach (var b in li.Blocks) b.Margin = new Thickness(b.Margin.Left, 0, 0, 2);
                wpf.ListItems.Add(li);
            }
            return wpf;
        }

        private Block Quote(Md.QuoteBlock quote)
        {
            var section = new Section
            {
                Margin          = new Thickness(0, 0, 0, 9),
                Padding         = new Thickness(12, 2, 8, 2),
                BorderBrush     = _accent,
                BorderThickness = new Thickness(3, 0, 0, 0),
                Background      = _inlineCodeBg
            };
            foreach (var child in quote)
                if (RenderBlock(child) is { } b)
                {
                    if (b is Paragraph) b.Foreground = _muted;
                    section.Blocks.Add(b);
                }
            return section;
        }

        private Block Rule() => new Paragraph
        {
            Margin          = new Thickness(0, 4, 0, 12),
            BorderBrush     = _border,
            BorderThickness = new Thickness(0, 0, 0, 1)
        };

        private Block CodeBlock(string code, string lang)
        {
            var p = new Paragraph
            {
                FontFamily      = new FontFamily("Cascadia Code, Consolas, monospace"),
                FontSize        = 12.5,
                Background      = _codeBg,
                Foreground      = _codeFg,
                Padding         = new Thickness(12, 9, 12, 9),
                Margin          = new Thickness(0, 0, 0, 10),
                LineHeight      = 18,
                BorderBrush     = _border,
                BorderThickness = new Thickness(1)
            };
            foreach (var inline in Highlight(code.TrimEnd('\n', '\r'), lang))
                p.Inlines.Add(inline);
            return p;
        }

        private Block BuildTable(Mdt.Table table)
        {
            var wpf = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 10) };

            int cols = 0;
            foreach (var r in table) if (r is Mdt.TableRow row) cols = Math.Max(cols, row.Count);
            for (int i = 0; i < cols; i++) wpf.Columns.Add(new TableColumn());

            var group = new TableRowGroup();
            wpf.RowGroups.Add(group);

            foreach (var r in table)
            {
                if (r is not Mdt.TableRow row) continue;
                var wRow = new TableRow();
                if (row.IsHeader) wRow.Background = _headerBg;

                int ci = 0;
                foreach (var c in row)
                {
                    if (c is not Mdt.TableCell cell) continue;
                    var align = ColumnAlign(table, ci);

                    var wCell = new TableCell
                    {
                        BorderBrush     = _border,
                        BorderThickness = new Thickness(0.6),
                        Padding         = new Thickness(9, 5, 9, 5)
                    };
                    foreach (var child in cell)
                        if (RenderBlock(child) is { } b)
                        {
                            b.Margin = new Thickness(0);
                            if (b is Paragraph par)
                            {
                                par.TextAlignment = align;
                                if (row.IsHeader) par.FontWeight = FontWeights.SemiBold;
                            }
                            wCell.Blocks.Add(b);
                        }
                    wRow.Cells.Add(wCell);
                    ci++;
                }
                group.Rows.Add(wRow);
            }
            return wpf;
        }

        private static TextAlignment ColumnAlign(Mdt.Table table, int index)
        {
            if (index < table.ColumnDefinitions.Count)
                return table.ColumnDefinitions[index].Alignment switch
                {
                    Mdt.TableColumnAlign.Center => TextAlignment.Center,
                    Mdt.TableColumnAlign.Right  => TextAlignment.Right,
                    _                           => TextAlignment.Left
                };
            return TextAlignment.Left;
        }

        private Block Fallback(Md.Block block)
        {
            // HTML-блоки и прочее показываем как обычный текст.
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 9) };
            if (block is Md.LeafBlock leaf)
                p.Inlines.Add(new Run(leaf.Lines.ToString().Trim()) { Foreground = _muted });
            return p;
        }

        // ── Инлайны ──────────────────────────────────────────────────────────

        private void AddInlines(InlineCollection target, Mdi.ContainerInline? container)
        {
            if (container is null) return;
            foreach (var inline in container)
                foreach (var wpf in RenderInline(inline))
                    target.Add(wpf);
        }

        private IEnumerable<Inline> RenderInline(Mdi.Inline inline)
        {
            switch (inline)
            {
                case Mdi.LiteralInline lit:
                    yield return new Run(lit.Content.ToString());
                    break;

                case Mdi.EmphasisInline em:
                    Span span = em.DelimiterChar == '~'
                        ? new Span { TextDecorations = TextDecorations.Strikethrough }
                        : em.DelimiterCount == 1 ? new Italic() : new Bold();
                    AddInlines(span.Inlines, em);
                    yield return span;
                    break;

                case Mdi.CodeInline code:
                    yield return new Run(code.Content)
                    {
                        FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"),
                        FontSize   = 12,
                        Background  = _inlineCodeBg,
                        Foreground = _accent
                    };
                    break;

                case Mdi.LinkInline { IsImage: true } img:
                    yield return new Run("🖼 ") { Foreground = _muted };
                    var alt = new Span { Foreground = _muted, FontStyle = FontStyles.Italic };
                    AddInlines(alt.Inlines, img);
                    yield return alt;
                    break;

                case Mdi.LinkInline link:
                    var h = new Hyperlink { Foreground = _accent };
                    AddInlines(h.Inlines, link);
                    if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) &&
                        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    {
                        h.NavigateUri = uri;
                        h.RequestNavigate += OnNavigate;
                        if (!string.IsNullOrEmpty(link.Title)) h.ToolTip = link.Title;
                    }
                    yield return h;
                    break;

                case Mdi.AutolinkInline auto:
                    var a = new Hyperlink(new Run(auto.Url)) { Foreground = _accent };
                    if (Uri.TryCreate(auto.Url, UriKind.Absolute, out var au))
                    {
                        a.NavigateUri = au;
                        a.RequestNavigate += OnNavigate;
                    }
                    yield return a;
                    break;

                case Mdi.LineBreakInline lb:
                    yield return lb.IsHard ? new LineBreak() : new Run(" ");
                    break;

                case Mdi.HtmlEntityInline ent:
                    yield return new Run(ent.Transcoded.ToString());
                    break;

                case Mdi.HtmlInline htmlTag:
                    // Встроенный HTML: <br> → перенос, остальные теги убираем (не показываем).
                    if (htmlTag.Tag.StartsWith("<br", StringComparison.OrdinalIgnoreCase))
                        yield return new LineBreak();
                    break;

                case Mdi.ContainerInline container:
                    var s = new Span();
                    AddInlines(s.Inlines, container);
                    yield return s;
                    break;
            }
        }

        private static void OnNavigate(object sender, RequestNavigateEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
            catch { /* нет браузера / битая ссылка — игнорируем */ }
            e.Handled = true;
        }

        // ── Подсветка кода ────────────────────────────────────────────────────

        private IEnumerable<Inline> Highlight(string code, string lang)
        {
            foreach (var (text, kind) in CodeLexer.Tokenize(code, lang))
            {
                var brush = kind switch
                {
                    TokenKind.Keyword => _kw,
                    TokenKind.String  => _str,
                    TokenKind.Comment => _comment,
                    TokenKind.Number  => _num,
                    TokenKind.Type    => _type,
                    _                 => _codeFg
                };
                // Разбиваем на строки, чтобы вставить переносы.
                var parts = text.Split('\n');
                for (int i = 0; i < parts.Length; i++)
                {
                    if (i > 0) yield return new LineBreak();
                    if (parts[i].Length == 0) continue;
                    var run = new Run(parts[i]) { Foreground = brush };
                    if (kind == TokenKind.Comment) run.FontStyle = FontStyles.Italic;
                    yield return run;
                }
            }
        }

        // ── Встроенный HTML → инлайны (без отображения тегов) ─────────────────

        private static readonly Regex RxComment = new(@"<!--.*?-->", RegexOptions.Singleline);
        private static readonly Regex RxScript  = new(@"<(script|style|svg)\b.*?</\1>",
                                                      RegexOptions.Singleline | RegexOptions.IgnoreCase);
        private static readonly Regex RxWs       = new(@"[ \t\r\n]+");
        private static readonly Regex RxHref     = new("href\\s*=\\s*[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase);

        private IEnumerable<Inline> HtmlToInlines(string html)
        {
            html = RxComment.Replace(html, "");
            html = RxScript.Replace(html, "");

            var mono   = new FontFamily("Cascadia Code, Consolas, monospace");
            var result = new List<Inline>();
            bool bold = false, italic = false, code = false, bullet = false, pendingBreak = false;
            string? href = null;

            void AddText(string raw)
            {
                var t = RxWs.Replace(WebUtility.HtmlDecode(raw), " ");
                if (string.IsNullOrWhiteSpace(t)) return;
                if (pendingBreak && result.Count > 0) result.Add(new LineBreak());
                pendingBreak = false;
                if (bullet) { t = "• " + t.TrimStart(); bullet = false; }

                var run = new Run(t);
                if (bold)   run.FontWeight = FontWeights.Bold;
                if (italic) run.FontStyle  = FontStyles.Italic;
                if (code)   { run.FontFamily = mono; run.Background = _inlineCodeBg; run.Foreground = _accent; }

                if (href is not null)
                {
                    var hl = new Hyperlink(run) { Foreground = _accent };
                    if (Uri.TryCreate(href, UriKind.Absolute, out var u) &&
                        (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
                    {
                        hl.NavigateUri = u;
                        hl.RequestNavigate += OnNavigate;
                    }
                    result.Add(hl);
                }
                else result.Add(run);
            }

            void HandleTag(string tag)
            {
                if (tag.Length == 0) return;
                bool close = tag[0] == '/';
                var body   = close ? tag[1..].TrimStart() : tag;
                int sp     = body.IndexOfAny(new[] { ' ', '\t', '\r', '\n', '/' });
                var name   = (sp < 0 ? body : body[..sp]).ToLowerInvariant();

                switch (name)
                {
                    case "br": result.Add(new LineBreak()); break;
                    case "b": case "strong": bold = !close; break;
                    case "i": case "em":     italic = !close; break;
                    case "code": case "kbd": case "tt": code = !close; break;
                    case "a":
                        if (close) href = null;
                        else { var m = RxHref.Match(tag); href = m.Success ? m.Groups[1].Value : null; }
                        break;
                    case "li":
                        if (!close) { pendingBreak = true; bullet = true; }
                        break;
                    case "p": case "div": case "section": case "article": case "header":
                    case "footer": case "ul": case "ol": case "tr": case "table":
                    case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                    case "blockquote": case "pre": case "hr":
                        pendingBreak = true; break;
                    // img, span, picture, source и прочее — пропускаем
                }
            }

            int i = 0, n = html.Length;
            while (i < n)
            {
                if (html[i] == '<')
                {
                    int j = html.IndexOf('>', i);
                    if (j < 0) { AddText(html[i..]); break; }
                    HandleTag(html[(i + 1)..j].Trim());
                    i = j + 1;
                }
                else
                {
                    int j = html.IndexOf('<', i);
                    if (j < 0) j = n;
                    AddText(html[i..j]);
                    i = j;
                }
            }
            return result;
        }

        // ── Утилиты ───────────────────────────────────────────────────────────

        private static Brush Find(string key, string fallback)
        {
            if (Application.Current?.TryFindResource(key) is Brush b) return b;
            return Hex(fallback);
        }

        private static SolidColorBrush Hex(string hex)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex)!;
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            return brush;
        }
    }
}

internal enum TokenKind { Plain, Keyword, String, Comment, Number, Type }

/// <summary>Простой межъязыковой лексер для подсветки блоков кода в README.</summary>
internal static class CodeLexer
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "var","let","const","function","return","if","else","for","while","do","switch","case",
        "break","continue","new","delete","class","interface","enum","struct","extends","implements",
        "import","export","from","default","async","await","yield","try","catch","finally","throw",
        "typeof","instanceof","in","of","void","null","undefined","true","false","this","super",
        "static","public","private","protected","internal","readonly","abstract","override","virtual",
        "get","set","namespace","using","def","elif","lambda","pass","with","as","is","not","and","or",
        "None","True","False","self","fn","mut","pub","impl","trait","match","use","mod","crate",
        "package","func","defer","go","chan","map","range","type","then","fi","done","esac","elif",
        "select","where","from"
    };

    private static readonly HashSet<string> HashComment = new(StringComparer.OrdinalIgnoreCase)
    {
        "bash","sh","shell","zsh","console","python","py","yaml","yml","ruby","rb","toml","ini",
        "dockerfile","docker","makefile","make","perl","r","powershell","ps1"
    };

    public static IEnumerable<(string text, TokenKind kind)> Tokenize(string code, string lang)
    {
        bool hash  = HashComment.Contains((lang ?? "").Trim());
        bool noCom = (lang ?? "").Trim().Equals("json", StringComparison.OrdinalIgnoreCase);

        int i = 0, n = code.Length;
        var buf = new StringBuilder();
        var tokens = new List<(string, TokenKind)>();
        void Plain(char c) => buf.Append(c);
        void Emit(string t, TokenKind k) { if (buf.Length > 0) { tokens.Add((buf.ToString(), TokenKind.Plain)); buf.Clear(); } tokens.Add((t, k)); }

        while (i < n)
        {
            char c = code[i];

            // Комментарии
            if (!noCom && c == '/' && i + 1 < n && code[i + 1] == '/')
            {
                int j = i; while (j < n && code[j] != '\n') j++;
                Emit(code[i..j], TokenKind.Comment); i = j; continue;
            }
            if (!noCom && c == '/' && i + 1 < n && code[i + 1] == '*')
            {
                int j = i + 2; while (j < n && !(code[j] == '*' && j + 1 < n && code[j + 1] == '/')) j++;
                j = Math.Min(n, (j < n ? j + 2 : j));
                Emit(code[i..j], TokenKind.Comment); i = j; continue;
            }
            if (!noCom && hash && c == '#')
            {
                int j = i; while (j < n && code[j] != '\n') j++;
                Emit(code[i..j], TokenKind.Comment); i = j; continue;
            }

            // Строки
            if (c is '"' or '\'' or '`')
            {
                int j = i + 1;
                while (j < n && code[j] != c)
                {
                    if (code[j] == '\\' && j + 1 < n) j++;
                    j++;
                }
                j = Math.Min(n, j + 1);
                Emit(code[i..j], TokenKind.String); i = j; continue;
            }

            // Числа
            if (char.IsDigit(c))
            {
                int j = i; while (j < n && (char.IsLetterOrDigit(code[j]) || code[j] is '.' or '_')) j++;
                Emit(code[i..j], TokenKind.Number); i = j; continue;
            }

            // Идентификаторы
            if (char.IsLetter(c) || c is '_' or '$')
            {
                int j = i; while (j < n && (char.IsLetterOrDigit(code[j]) || code[j] is '_' or '$')) j++;
                var word = code[i..j];
                var kind = Keywords.Contains(word)            ? TokenKind.Keyword
                         : char.IsUpper(word[0])              ? TokenKind.Type
                         :                                      TokenKind.Plain;
                if (kind == TokenKind.Plain) buf.Append(word);
                else Emit(word, kind);
                i = j; continue;
            }

            Plain(c); i++;
        }
        if (buf.Length > 0) tokens.Add((buf.ToString(), TokenKind.Plain));
        return tokens;
    }
}
