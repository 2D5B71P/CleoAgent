using System;
using System.Text;

namespace CleoAgent.Core.Web;

// Heuristic gate deciding whether an extracted page is worth keeping. Called on
// the primary provider's output: if it fails here, the WebFetchTool falls back
// to the configured provider_fallback (or errors if there is none).
//
// The ReverseMarkdown "default" provider just converts whatever HTML it got. A
// JS-rendered SPA, a login wall, or an empty body converts to empty/near-empty
// markdown, which this flags so the tool can try a smarter provider instead.
internal static class Readability
{
    // Minimum non-whitespace characters the extracted content should contain.
    // Anything below this is treated as "nothing useful fetched".
    public const int MinMeaningfulChars = 300;

    // Above this fraction (vs total content) of "shallow" lines (links, nav,
    // boilerplate) the page is considered low-value chrome.
    private const double MaxShallowRatio = 0.85;

    // Returns true when the content looks like a real, readable page.
    public static bool Passes(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return false;
        }

        string trimmed = markdown.Trim();

        // 1. Size gate: not enough real text to be useful.
        int meaningful = CountMeaningful(trimmed);
        if (meaningful < MinMeaningfulChars)
        {
            return false;
        }

        // 2. Density gate: too much of the content is link/boilerplate chrome.
        int shallow = CountShallowLines(trimmed);
        double shallowRatio = (double)shallow / Math.Max(1, CountNonEmptyLines(trimmed));
        if (shallowRatio > MaxShallowRatio)
        {
            return false;
        }

        return true;
    }

    // Non-whitespace ASCII/Unicode characters, ignoring markdown syntax noise
    // (#, *, >, |, etc.) so a page of table/header dressing doesn't count as text.
    private static int CountMeaningful(string text)
    {
        int count = 0;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            if (c is '#' or '*' or '>' or '|' or '-' or '_' or '`' or '~' or '[' or ']' or '(' or ')' or '=')
            {
                continue;
            }

            count++;
        }

        return count;
    }

    // Lines that look like navigation chrome rather than prose: link-only lines,
    // very short lines, list fragments, and table separators.
    private static int CountShallowLines(string text)
    {
        int count = 0;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (IsShallowLine(line))
            {
                count++;
            }
        }

        return count;
    }

    private static bool IsShallowLine(string line)
    {
        if (line.Length <= 3)
        {
            return true; // "---", "|", "ok" etc.
        }

        if (line is "---" or "***" or "===")
        {
            return true; // horizontal rules / table separators
        }

        // A line that is (almost) pure link markup: [text](url) recycling.
        int linkChars = CountLinkChars(line);
        return (double)linkChars / line.Length > 0.8;
    }

    private static int CountLinkChars(string line)
    {
        int count = 0;
        foreach (char c in line)
        {
            if (c is '[' or ']' or '(' or ')')
            {
                count++;
            }
        }

        return count;
    }

    private static int CountNonEmptyLines(string text)
    {
        int count = 0;
        foreach (string raw in text.Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(raw))
            {
                count++;
            }
        }

        return count;
    }
}