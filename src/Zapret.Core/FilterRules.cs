using System.Text.RegularExpressions;

namespace Zapret.Core;

public static class FilterRules
{
    public static bool ValidPorts(string value)
    {
        if (!Regex.IsMatch(value, @"^\d+(?:-\d+)?(?:,\d+(?:-\d+)?)*$")) return false;
        foreach (var range in value.Split(','))
        {
            var ends = range.Split('-');
            if (!int.TryParse(ends[0], out var first) || first is < 1 or > 65535) return false;
            if (ends.Length == 2 && (!int.TryParse(ends[1], out var last) || last < first || last > 65535)) return false;
        }
        return true;
    }

    public static void Validate(GameFilterSettings game, IpSetMode mode)
    {
        if (!Enum.IsDefined(game.Mode) || !Enum.IsDefined(mode)) throw new ArgumentException("Выберите допустимый режим фильтра.");
        if (!ValidPorts(game.TcpPorts) || !ValidPorts(game.UdpPorts))
            throw new ArgumentException("Проверьте порты: числа от 1 до 65535, например 443,1024-65535.");
    }
}
