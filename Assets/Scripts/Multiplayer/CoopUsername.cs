using System.Collections.Generic;
using System.Text;
using UnityEngine;

public static class CoopUsername
{
    public const int MaxLength = 24;
    private const string Preference = "Coop.Username";
    public static string Local
    {
        get
        {
            // Also permits isolated multi-process validation without changing preferences.
            var args = System.Environment.GetCommandLineArgs();
            int index = System.Array.IndexOf(args, "-coopUsername");
            return Clean(index >= 0 && index + 1 < args.Length ? Unescape(args[index + 1]) : PlayerPrefs.GetString(Preference, ""));
        }
        set { PlayerPrefs.SetString(Preference, Clean(value)); PlayerPrefs.Save(); }
    }
    /// <summary>
    /// Decodes percent-escaped UTF-8 in the command line override. On Windows the player
    /// receives its command line through the system ANSI code page, so a launcher that hands
    /// over non-ASCII text (e.g. a Chinese name) arrives as mojibake. Escaping the UTF-8 bytes
    /// keeps such names intact for scripted sessions; unescaped text is returned untouched.
    /// </summary>
    public static string Unescape(string value)
    {
        if (string.IsNullOrEmpty(value) || value.IndexOf('%') < 0) return value;
        var bytes = new List<byte>(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '%' && i + 2 < value.Length && Hex(value[i + 1]) >= 0 && Hex(value[i + 2]) >= 0)
            {
                bytes.Add((byte)(Hex(value[i + 1]) * 16 + Hex(value[i + 2])));
                i += 2;
            }
            else bytes.AddRange(Encoding.UTF8.GetBytes(value[i].ToString()));
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
    private static int Hex(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    }
    public static string Clean(string value)
    {
        var result = new StringBuilder();
        if (value != null)
            foreach (char c in value)
            {
                if (result.Length >= MaxLength) break;
                if (!char.IsControl(c) && !char.IsSurrogate(c) && c != '<' && c != '>') result.Append(c);
            }
        return result.ToString().Trim();
    }
}
