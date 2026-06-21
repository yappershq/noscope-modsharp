using System;
using System.Collections.Generic;
using Sharp.Shared.Definition;

namespace NoScope;

/// <summary>
///     Replaces <c>{color}</c> placeholders (e.g. <c>{green}</c>, <c>{red}</c>,
///     <c>{default}</c>) with the actual <see cref="ChatColor" /> control characters.
///     Used as the <c>Transform</c> step on localized messages — the locale file
///     double-braces the tokens (<c>{{green}}</c>) so <c>string.Format</c> collapses
///     them to single-braced literals before this runs.
///     Canonical impl copied from SuperPowers.Shared.Extensions.ChatFormat.
/// </summary>
internal static class ChatFormat
{
    private static readonly Dictionary<string, string> ColorCache = new(
        StringComparer.OrdinalIgnoreCase)
    {
        { "{white}",      ChatColor.White },
        { "{default}",    ChatColor.White },
        { "{darkred}",    ChatColor.DarkRed },
        { "{pink}",       ChatColor.Pink },
        { "{green}",      ChatColor.Green },
        { "{lightgreen}", ChatColor.LightGreen },
        { "{lime}",       ChatColor.Lime },
        { "{red}",        ChatColor.Red },
        { "{grey}",       ChatColor.Grey },
        { "{gray}",       ChatColor.Grey },
        { "{yellow}",     ChatColor.Yellow },
        { "{gold}",       ChatColor.Gold },
        { "{silver}",     ChatColor.Silver },
        { "{blue}",       ChatColor.Blue },
        { "{lightblue}",  ChatColor.Blue },
        { "{darkblue}",   ChatColor.DarkBlue },
        { "{purple}",     ChatColor.Purple },
        { "{lightred}",   ChatColor.LightRed },
        { "{muted}",      ChatColor.Muted },
        { "{head}",       ChatColor.Head },
        { "{whitespace}", " " },
    };

    /// <summary>
    ///     Replace color placeholders like <c>{red}</c>, <c>{blue}</c> with actual
    ///     <see cref="ChatColor" /> codes for chat messages.
    /// </summary>
    public static string ProcessColorCodes(string message)
    {
        if (string.IsNullOrEmpty(message))
            return message;

        if (!message.Contains('{'))
            return message;

        var result = message;

        foreach (var (placeholder, code) in ColorCache)
            result = result.Replace(placeholder, code, StringComparison.OrdinalIgnoreCase);

        return result;
    }
}
