# Сторонние компоненты

Код Zapret Desktop распространяется по [MIT](LICENSE). Включённые библиотеки, шрифты и дистрибутив `zapret` сохраняют лицензии своих авторов.

| Компонент | Исходный проект | Тексты лицензий и уведомления |
| --- | --- | --- |
| zapret и Windows-дистрибутив Flowseal | [bol-van/zapret](https://github.com/bol-van/zapret), [Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube) | MIT: `zapret/LICENSE.txt` в репозитории, `licenses/Zapret-LICENSE.txt` в ZIP |
| WinDivert 2.2.2 | [basil00/WinDivert](https://github.com/basil00/WinDivert/tree/v2.2.2) | LGPLv3 или GPLv2: [WinDivert-LICENSE.txt](licenses/WinDivert-LICENSE.txt) |
| Cygwin 3.4.10 (`cygwin1.dll`) | [newlib-cygwin](https://github.com/mirror/newlib-cygwin/tree/cygwin-3.4.10) | LGPLv3+ и Cygwin Linking Exception: [уведомление](licenses/Cygwin-LICENSE.txt), [LGPLv3](licenses/LGPL-3.0.txt), [GPLv3](licenses/GPL-3.0.txt) |
| Avalonia 12.1.3 | [AvaloniaUI/Avalonia](https://github.com/AvaloniaUI/Avalonia/tree/12.1.3) | MIT: [Avalonia-LICENSE.md](licenses/Avalonia-LICENSE.md) |
| .NET Runtime и Microsoft.Extensions | [dotnet/runtime](https://github.com/dotnet/runtime) | MIT: [DotNet-LICENSE.txt](licenses/DotNet-LICENSE.txt); дополнительные уведомления — [Runtime-NOTICES.txt](licenses/Runtime-NOTICES.txt) |
| CommunityToolkit.Mvvm 8.3.2 | [CommunityToolkit/dotnet](https://github.com/CommunityToolkit/dotnet/tree/v8.3.2) | MIT и уведомления пакета: [Runtime-NOTICES.txt](licenses/Runtime-NOTICES.txt) |
| SkiaSharp 3.119.4 и HarfBuzzSharp 8.3.1.3 | [mono/SkiaSharp](https://github.com/mono/SkiaSharp) | MIT и лицензии включённых нативных компонентов: [Runtime-NOTICES.txt](licenses/Runtime-NOTICES.txt) |
| ANGLE | [google/angle](https://github.com/google/angle) | BSD: [Runtime-NOTICES.txt](licenses/Runtime-NOTICES.txt) |
| MicroCom.Runtime 0.11.6 | [kekekeks/MicroCom](https://github.com/kekekeks/MicroCom) | MIT: [Runtime-NOTICES.txt](licenses/Runtime-NOTICES.txt) |
| Tmds.DBus.Protocol 0.94.1 | [tmds/Tmds.DBus](https://github.com/tmds/Tmds.DBus) | MIT: [Runtime-NOTICES.txt](licenses/Runtime-NOTICES.txt) |
| Inter | [rsms/inter](https://github.com/rsms/inter) | SIL Open Font License 1.1: [Inter-OFL.txt](licenses/Inter-OFL.txt) |
| Fluent UI System Icons | [microsoft/fluentui-system-icons](https://github.com/microsoft/fluentui-system-icons) | MIT: [FluentIcons-LICENSE.txt](licenses/FluentIcons-LICENSE.txt) |

В ZIP входят `LICENSE`, этот указатель и общая папка `licenses/`. `Runtime-NOTICES.txt` объединяет исходные тексты дополнительных уведомлений зависимостей; одинаковые тексты включены один раз. При обновлении зависимостей нужно обновлять и соответствующие уведомления.

Исходники распространяемых нативных библиотек: [WinDivert 2.2.2](https://github.com/basil00/WinDivert/archive/refs/tags/v2.2.2.tar.gz), [Cygwin 3.4.10](https://github.com/mirror/newlib-cygwin/archive/refs/tags/cygwin-3.4.10.tar.gz). DLL остаются отдельными файлами и могут быть заменены совместимой сборкой. Условия Cygwin и исключение для связывания опубликованы на [официальной странице лицензирования](https://cygwin.com/licensing.html).
