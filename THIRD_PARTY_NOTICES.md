# Сторонние компоненты и права авторов

Лицензия [MIT для Zapret Desktop](LICENSE) распространяется на код приложения, созданный fecset. Папка `zapret/`, драйвер и библиотеки сохраняют собственные условия использования. Этот файл помогает найти исходные проекты и тексты лицензий; он не заменяет их.

| Компонент | Автор / источник | Лицензия и уведомление |
| --- | --- | --- |
| zapret | [bol-van](https://github.com/bol-van/zapret) | MIT; в исходниках текст находится в `zapret/LICENSE.txt`, в выпуске — в `licenses/Zapret-LICENSE.txt`. |
| Windows-дистрибутив и стратегии zapret | [Flowseal](https://github.com/Flowseal/zapret-discord-youtube) | MIT; авторские права и дополнительные сведения находятся в том же файле. |
| WinDivert (`WinDivert.dll`, `WinDivert64.sys`) | [basil00](https://github.com/basil00/WinDivert) | LGPLv3 или GPLv2 на выбор; [копия официального текста лицензии](licenses/WinDivert-LICENSE.txt) и [исходный код](https://github.com/basil00/WinDivert). |
| Cygwin (`cygwin1.dll`, версия 3.4.10) | [Cygwin contributors](https://cygwin.com/) | LGPLv3 or later с Cygwin Linking Exception; [уведомление](licenses/Cygwin-LICENSE.txt), [LGPLv3](licenses/LGPL-3.0.txt), [GPLv3](licenses/GPL-3.0.txt). [Исходники именно 3.4.10](https://github.com/mirror/newlib-cygwin/tree/cygwin-3.4.10), [скачать исходный код](https://github.com/mirror/newlib-cygwin/archive/refs/tags/cygwin-3.4.10.tar.gz), [официальный Git](https://cygwin.com/git.html). |
| Fluent UI System Icons | [Microsoft](https://github.com/microsoft/fluentui-system-icons) | MIT; [копия лицензии](licenses/FluentIcons-LICENSE.txt). |
| Avalonia | [AvaloniaUI](https://github.com/AvaloniaUI/Avalonia) | MIT; [копия лицензии](licenses/Avalonia-LICENSE.md). |
| Inter | [The Inter Project Authors](https://github.com/rsms/inter) | SIL Open Font License 1.1; [копия лицензии](licenses/Inter-OFL.txt). |
| .NET Runtime | [Microsoft и участники .NET](https://github.com/dotnet/runtime) | MIT; [копия лицензии](licenses/DotNet-LICENSE.txt). |

В архив выпуска входят `LICENSE`, этот файл и папка `licenses/` с текстами лицензий. Cygwin DLL поставляется отдельным файлом, который можно заменить совместимой сборкой из указанных исходников. WinDivert из комплекта Flowseal 1.10.3: [исходники версии 2.2.2](https://github.com/basil00/WinDivert/tree/v2.2.2), [скачать исходный код](https://github.com/basil00/WinDivert/archive/refs/tags/v2.2.2.tar.gz). Условия Cygwin описаны на [официальной странице лицензирования](https://cygwin.com/licensing.html). Лицензии остальных пакетов NuGet указаны в их метаданных и репозиториях.
