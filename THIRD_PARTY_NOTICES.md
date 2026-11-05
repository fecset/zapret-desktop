# Сторонние компоненты

Код Zapret Desktop распространяется по [MIT](LICENSE). Включённые библиотеки, шрифты и дистрибутив `zapret` сохраняют лицензии своих авторов.

| Компонент | Исходный проект | Тексты лицензий и уведомления |
| --- | --- | --- |
| zapret / winws v72.9 и Windows-дистрибутив Flowseal | [bol-van/zapret](https://github.com/bol-van/zapret/tree/c849e55ef0f1c244206f5a05ff7b1ab41a3824ee), [Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube) | MIT: `zapret/LICENSE.txt` в репозитории, `licenses/Zapret-LICENSE.txt` в ZIP; включённые SHA/HMAC/HKDF, kavl, uthash, AES/GCM и zlib 1.3.1: [Winws-NOTICES.txt](licenses/Winws-NOTICES.txt) |
| WinDivert 2.2.2 | [basil00/WinDivert](https://github.com/basil00/WinDivert/tree/v2.2.2) | LGPLv3 или GPLv2: [WinDivert-LICENSE.txt](licenses/WinDivert-LICENSE.txt) |
| Cygwin 3.4.10 (`cygwin1.dll`) | [newlib-cygwin](https://github.com/mirror/newlib-cygwin/tree/cygwin-3.4.10) | LGPLv3+ и Cygwin Linking Exception: [уведомление](licenses/Cygwin-LICENSE.txt), [LGPLv3](licenses/LGPL-3.0.txt), [GPLv3](licenses/GPL-3.0.txt); лицензии встроенной Newlib: [Cygwin-NEWLIB-NOTICES.txt](licenses/Cygwin-NEWLIB-NOTICES.txt) |
| Avalonia 12.1.3 | [AvaloniaUI/Avalonia](https://github.com/AvaloniaUI/Avalonia/tree/12.1.3) | MIT: [Avalonia-LICENSE.md](licenses/Avalonia-LICENSE.md) |
| .NET Runtime 10.0.12 и Microsoft.Extensions 10.0.0 | [dotnet/runtime](https://github.com/dotnet/runtime) | MIT: [DotNet-LICENSE.txt](licenses/DotNet-LICENSE.txt); дополнительные уведомления — [Runtime-NOTICES.txt](licenses/Runtime-NOTICES.txt) |
| CommunityToolkit.Mvvm 8.3.2 | [CommunityToolkit/dotnet](https://github.com/CommunityToolkit/dotnet/tree/v8.3.2) | MIT и уведомления пакета: [Runtime-NOTICES.txt](licenses/Runtime-NOTICES.txt) |
| SkiaSharp 3.119.4 и HarfBuzzSharp 8.3.1.3 | [mono/SkiaSharp](https://github.com/mono/SkiaSharp) | MIT и лицензии включённых нативных компонентов: [Runtime-NOTICES.txt](licenses/Runtime-NOTICES.txt) |
| ANGLE | [google/angle](https://github.com/google/angle) | BSD: [Runtime-NOTICES.txt](licenses/Runtime-NOTICES.txt) |
| MicroCom.Runtime 0.11.6 | [kekekeks/MicroCom](https://github.com/kekekeks/MicroCom) | MIT: [Runtime-NOTICES.txt](licenses/Runtime-NOTICES.txt) |
| Tmds.DBus.Protocol 0.94.1 | [tmds/Tmds.DBus](https://github.com/tmds/Tmds.DBus) | MIT: [Runtime-NOTICES.txt](licenses/Runtime-NOTICES.txt) |
| Inter | [rsms/inter](https://github.com/rsms/inter) | SIL Open Font License 1.1: [Inter-OFL.txt](licenses/Inter-OFL.txt) |
| JetBrains Mono 2.305 Regular / Medium | [JetBrains/JetBrainsMono](https://github.com/JetBrains/JetBrainsMono) | SIL Open Font License 1.1: [JetBrainsMono-OFL.txt](licenses/JetBrainsMono-OFL.txt) |
| Fluent UI System Icons | [microsoft/fluentui-system-icons](https://github.com/microsoft/fluentui-system-icons) | MIT: [FluentIcons-LICENSE.txt](licenses/FluentIcons-LICENSE.txt) |

В ZIP входят `LICENSE`, этот указатель и общая папка `licenses/`. `Runtime-NOTICES.txt` объединяет исходные тексты дополнительных уведомлений зависимостей; одинаковые тексты включены один раз. При обновлении зависимостей нужно обновлять и соответствующие уведомления.

Исходники распространяемых нативных библиотек: [WinDivert 2.2.2](https://github.com/basil00/WinDivert/archive/refs/tags/v2.2.2.tar.gz), [Cygwin 3.4.10](https://github.com/mirror/newlib-cygwin/archive/refs/tags/cygwin-3.4.10.tar.gz). DLL остаются отдельными файлами и могут быть заменены совместимой сборкой. Условия Cygwin и исключение для связывания опубликованы на [официальной странице лицензирования](https://cygwin.com/licensing.html).

## TG WS Proxy — Flowseal

- Original author: Flowseal.
- Source: https://github.com/Flowseal/tg-ws-proxy
- License: MIT; complete original copyright and license: licenses/TG-WS-Proxy-LICENSE.txt.
- C# port/adaptation of the networking functionality, bundled inside Zapret Desktop. Original source revision: 2085861957070eeea07990a522b29f42e5230210. Original authorship remains with Flowseal.
- Native .NET transports replace Python dependencies; no Python runtime or upstream EXE is redistributed. See docs/TELEGRAM_PROXY.md for scope and differences.
- System.Security.Cryptography.ProtectedData 10.0.0 is a Microsoft .NET library under MIT; covered by licenses/DotNet-LICENSE.txt and licenses/Runtime-NOTICES.txt.

## Telegram logo

The original Telegram vector logo is used for Telegram Proxy navigation and controls. Source and usage terms: https://telegram.org/tour/screenshots (official Telegram logos). TelegramMark.cs uses the original airplane silhouette with the application’s monochrome icon colors; this feature is not affiliated with Telegram.
