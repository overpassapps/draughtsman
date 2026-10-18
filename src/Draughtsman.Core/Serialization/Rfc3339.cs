namespace Draughtsman.Core.Serialization;

/// <summary>
/// The schema's <c>date-time</c> format: RFC 3339, as the schema library this replaced judged it (docs/adr/0004-document-schema-validator.md, pinned
/// by the corpus). It is a little wider than the RFC where the library was: <c>_</c> or a space may stand for the <c>T</c>, <c>t</c> and <c>z</c> are
/// accepted, the fraction may have any number of digits, a final line feed is tolerated (a regular expression's <c>$</c>), the proleptic Gregorian
/// calendar runs from year 0000, and a leap second is accepted only when it falls on 23:59:60 UTC once the offset is applied. Digits are ASCII only.
/// </summary>
internal static class Rfc3339
{
    public static bool IsDateTime(string text)
    {
        ReadOnlySpan<char> s = text;
        if (s.Length > 0 && s[^1] == '\n')
        {
            s = s[..^1];
        }

        // yyyy-MM-dd, one separator, HH:mm:ss, optional .fraction, then Z or +HH:mm / -HH:mm.
        if (s.Length < 20 || !Digits(s, 0, 4) || s[4] != '-' || !Digits(s, 5, 2) || s[7] != '-' || !Digits(s, 8, 2) || s[10] is not ('T' or 't' or '_' or ' ')
            || !Digits(s, 11, 2) || s[13] != ':' || !Digits(s, 14, 2) || s[16] != ':' || !Digits(s, 17, 2))
        {
            return false;
        }

        int year = Number(s, 0, 4);
        int month = Number(s, 5, 2);
        int day = Number(s, 8, 2);
        int hour = Number(s, 11, 2);
        int minute = Number(s, 14, 2);
        int second = Number(s, 17, 2);

        int position = 19;
        if (s[position] == '.')
        {
            int fractionStart = ++position;
            while (position < s.Length && s[position] is >= '0' and <= '9')
            {
                position++;
            }

            if (position == fractionStart)
            {
                return false;
            }
        }

        ReadOnlySpan<char> offset = s[position..];
        bool zulu = offset is "Z" or "z";
        if (!zulu && !(offset.Length == 6 && offset[0] is '+' or '-' && Digits(offset, 1, 2) && offset[3] == ':' && Digits(offset, 4, 2)))
        {
            return false;
        }

        if (month is < 1 or > 12 || day < 1 || day > DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 60)
        {
            return false;
        }

        int offsetHour = zulu ? 0 : Number(offset, 1, 2);
        int offsetMinute = zulu ? 0 : Number(offset, 4, 2);
        if (offsetHour > 23 || offsetMinute > 59)
        {
            return false;
        }

        return second != 60 || IsLastMinuteOfUtcDay(hour, minute, offsetHour, offsetMinute, !zulu && offset[0] == '+');
    }

    /// <summary>A leap second may only be the one at the end of 23:59 UTC: the local time minus the offset must be 23:59.</summary>
    private static bool IsLastMinuteOfUtcDay(int hour, int minute, int offsetHour, int offsetMinute, bool positive)
    {
        int utcHour = positive ? hour - offsetHour : hour + offsetHour;
        int utcMinute = positive ? minute - offsetMinute : minute + offsetMinute;
        if (utcMinute < 0)
        {
            utcMinute += 60;
            utcHour--;
        }
        else if (utcMinute >= 60)
        {
            utcMinute -= 60;
            utcHour++;
        }

        utcHour %= 24;
        if (utcHour < 0)
        {
            utcHour += 24;
        }

        return utcHour == 23 && utcMinute == 59;
    }

    private static int DaysInMonth(int year, int month)
    {
        return month switch
        {
            2 => year % 4 == 0 && (year % 100 != 0 || year % 400 == 0) ? 29 : 28,
            4 or 6 or 9 or 11 => 30,
            _ => 31,
        };
    }

    private static bool Digits(ReadOnlySpan<char> s, int start, int count)
    {
        if (start + count > s.Length)
        {
            return false;
        }

        for (int i = start; i < start + count; i++)
        {
            if (s[i] is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static int Number(ReadOnlySpan<char> s, int start, int count)
    {
        int value = 0;
        for (int i = start; i < start + count; i++)
        {
            value = (value * 10) + (s[i] - '0');
        }

        return value;
    }
}
