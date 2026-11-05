using Zapret.Core;
using Zapret.TelegramProxy.MtProto;
namespace Zapret.TelegramProxy.Routing;

internal static class DcRoutes
{
    public static readonly IReadOnlyDictionary<int,string> Production = new Dictionary<int,string>
    { [1]="149.154.175.50", [2]="149.154.167.51", [3]="149.154.175.100", [4]="149.154.167.91", [5]="149.154.171.5", [203]="91.105.192.100" };
    public static readonly IReadOnlyDictionary<int,string> Test = new Dictionary<int,string>
    { [1]="149.154.175.10", [2]="149.154.167.40", [3]="149.154.175.117" };
    public static readonly string[] DefaultCfDomains = ["pclead.co.uk","offshor.co.uk","cakeisalie.co.uk","noskomnadzor.co.uk","lovetrue.co.uk","sorokdva.co.uk","pyatdesyatdva.co.uk","kartoshka.co.uk","sorokodin.co.uk","pyatdesyatodin.co.uk","notelega.co.uk","ebally.co.uk","nebally.co.uk","havegreatday.co.uk","pomogite.co.uk","fixtelega.co.uk","sadnews.co.uk","onedaychamp.co.uk","stopblocking.co.uk","nothingthere.co.uk"];
    public static IEnumerable<string> WsDomains(ClientHello hello)
    {
        var id = hello.Dc == 203 ? 2 : hello.Dc;
        if (hello.Media) yield return $"kws{id}-1.web.telegram.org";
        yield return $"kws{id}.web.telegram.org";
    }
}
