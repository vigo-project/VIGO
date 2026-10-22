using System.Buffers;
using System.Buffers.Text;
using System.IO.Pipelines;
using System.Text;
using Vigo.Core.Models;

namespace Vigo.Git.Log;

/// <summary>
/// Parses the output of <c>git log -z --format=%H%x00%P%x00%an%x00%ae%x00%at%x00%s</c> straight from the pipe:
/// NUL-separated fields, six per commit, records terminated by NUL. Nothing is buffered beyond the current field.
/// </summary>
internal sealed class CommitLogReader
{
    private const int FieldCount = 6;
    private const byte Separator = 0;

    /// <summary>Arguments for git.exe; refs/stash, notes, maintenance prefetch refs and AI agent checkpoints (refs/agents/*) are left out of the graph.</summary>
    public static IReadOnlyList<string> BuildArguments(CommitLogOptions options)
    {
        List<string> args =
        [
            "log", "--topo-order", "-z", "--no-color", "--no-show-signature", "--encoding=UTF-8",
            "--format=%H%x00%P%x00%an%x00%ae%x00%at%x00%s",
        ];
        if (options.MaxCount is { } max) args.Add($"--max-count={max}");

        var filter = options.Filter;
        if (filter is { Terms.Count: > 0 } or { Authors.Count: > 0 })
        {
            // Words and names are plain text, not regular expressions.
            args.AddRange(["--regexp-ignore-case", "--fixed-strings"]);
            args.AddRange(filter.Terms.Select(term => $"--grep={term}"));
            if (filter.Terms.Count > 1) args.Add("--all-match");
            args.AddRange(filter.Authors.Select(author => $"--author={author}"));
        }

        // An id (or prefix) shows that commit alone; the rest searches every ref.
        if (filter?.Sha is { } sha) args.AddRange(["--no-walk", sha]);
        else
        {
            args.AddRange(["--exclude=refs/stash", "--exclude=refs/notes/*", "--exclude=refs/prefetch/*", "--exclude=refs/agents/*"]);
            // Refs hidden by the user (full names, no wildcard in a valid ref name): git leaves out the commits only they reach.
            if (options.HiddenRefs is { Count: > 0 } hidden) args.AddRange(hidden.Select(name => "--exclude=" + name));
            args.Add("--all");
        }

        args.Add("--");
        if (filter is not null) args.AddRange(filter.Files.Select(PathFragment));
        return args;
    }

    // Any path containing the text, whatever the case. Wildcards stay active (file:*.md), and a Windows separator
    // typed by the user is a git separator.
    private static string PathFragment(string text) => $":(icase)*{text.Replace('\\', '/')}*";

    private readonly int _pageSize;
    private readonly CommitPageHandler _onPage;
    private readonly Dictionary<string, string> _interned = new(StringComparer.Ordinal);
    private readonly List<GitSha> _parents = new(2);

    private List<CommitInfo> _page;
    private int _field;
    private GitSha _sha;
    private string _authorName = string.Empty;
    private string _authorEmail = string.Empty;
    private DateTimeOffset _authorDate;

    public CommitLogReader(int pageSize, CommitPageHandler onPage)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        _pageSize = pageSize;
        _onPage = onPage;
        _page = new List<CommitInfo>(pageSize);
    }

    /// <summary>Reads the pipe to the end, delivering full pages as they fill and the last partial page at the end.</summary>
    public async Task ReadAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            var read = await reader.ReadAsync(cancellationToken);
            var buffer = read.Buffer;
            var consumed = ParseFields(buffer);

            if (_page.Count >= _pageSize)
            {
                // Complete fields may remain: leave them unexamined so the next read returns at once.
                reader.AdvanceTo(consumed);
                await FlushAsync(cancellationToken);
                continue;
            }

            if (!read.IsCompleted)
            {
                reader.AdvanceTo(consumed, buffer.End);
            }
            else
            {
                // The last record may lack its terminator: whatever is left is its subject.
                var rest = buffer.Slice(consumed);
                if (_field == FieldCount - 1) AcceptField(rest);
                else if (_field != 0 || !rest.IsEmpty) throw new FormatException("Truncated git log output.");
                reader.AdvanceTo(rest.End);
                break;
            }
        }
        if (_page.Count > 0) await FlushAsync(cancellationToken);
    }

    /// <summary>Consumes every complete field of the buffer; returns the position after the last separator.</summary>
    private SequencePosition ParseFields(ReadOnlySequence<byte> buffer)
    {
        var sequenceReader = new SequenceReader<byte>(buffer);
        while (sequenceReader.TryReadTo(out ReadOnlySequence<byte> field, Separator))
        {
            AcceptField(field);
            // Stop at a page boundary so the caller can deliver it before reading further.
            if (_page.Count >= _pageSize) break;
        }
        return sequenceReader.Position;
    }

    private void AcceptField(ReadOnlySequence<byte> sequence)
    {
        byte[]? rented = null;
        var field = sequence.IsSingleSegment ? sequence.FirstSpan : Linearize(sequence, out rented);
        try
        {
            switch (_field)
            {
                case 0:
                    if (!GitSha.TryParse(field, out _sha)) throw new FormatException("Invalid commit id in git log output.");
                    break;
                case 1:
                    ParseParents(field);
                    break;
                case 2:
                    _authorName = Intern(field);
                    break;
                case 3:
                    _authorEmail = Intern(field);
                    break;
                case 4:
                    _authorDate = Utf8Parser.TryParse(field, out long seconds, out _)
                        ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                        : default;
                    break;
                case 5:
                    _page.Add(new CommitInfo(_sha, _parents.Count == 0 ? [] : _parents.ToArray(),
                        _authorName, _authorEmail, _authorDate, Encoding.UTF8.GetString(field)));
                    break;
            }
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
        _field = (_field + 1) % FieldCount;
    }

    private void ParseParents(ReadOnlySpan<byte> field)
    {
        _parents.Clear();
        foreach (var range in field.Split((byte)' '))
        {
            var hex = field[range];
            if (hex.IsEmpty) continue;
            if (!GitSha.TryParse(hex, out var parent)) throw new FormatException("Invalid parent id in git log output.");
            _parents.Add(parent);
        }
    }

    private string Intern(ReadOnlySpan<byte> utf8)
    {
        Span<char> chars = utf8.Length <= 256 ? stackalloc char[utf8.Length] : new char[utf8.Length];
        var written = Encoding.UTF8.GetChars(utf8, chars);
        var text = chars[..written];
        var lookup = _interned.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.TryGetValue(text, out var existing)) return existing;
        var value = text.ToString();
        _interned.Add(value, value);
        return value;
    }

    private static ReadOnlySpan<byte> Linearize(ReadOnlySequence<byte> sequence, out byte[] rented)
    {
        rented = ArrayPool<byte>.Shared.Rent((int)sequence.Length);
        sequence.CopyTo(rented);
        return rented.AsSpan(0, (int)sequence.Length);
    }

    private async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        var page = _page;
        _page = new List<CommitInfo>(_pageSize);
        await _onPage(page, cancellationToken);
    }
}
