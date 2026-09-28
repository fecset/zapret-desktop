# Zapret Desktop architecture and upstream map

## Source of truth

The bundled `zapret/` directory is a Flowseal distribution with `service.bat`
version 1.10.3, 22 `general*.bat` strategies, `bin/`, `lists/`, and
`utils/test zapret.ps1`. This local copy omits the upstream `README.md`,
`.service/`, and `LICENSE.txt`; their current upstream versions were inspected
at <https://github.com/Flowseal/zapret-discord-youtube>. The executable and
driver remain upstream components. Desktop must never reinterpret their DPI
semantics.

| Existing feature | Source | Desktop implementation |
| --- | --- | --- |
| Manual strategy launch | `general*.bat` | Discover and parse the `winws.exe` invocation into argument tokens; launch `bin/winws.exe` with `ArgumentList` and the `bin` working directory. Reject unknown batch constructs. |
| Strategy selection/service install | `service.bat :service_install` | Reuse the parsed strategy for `zapret` service installation; persist the selected source name in the existing registry value. |
| Process/service status | `:status_zapret`, `:service_status` | Query the OS on each refresh. Distinguish a Desktop-owned process, an external `winws`, and the `zapret` service. Never terminate an external instance as a side effect of normal Stop. |
| Game Filter | `:game_switch_status`, `:game_switch` | Read/write `utils/game_filter.enabled`. Actual modes are `disabled`, `all`, `tcp`, `udp`. Disabled injects port `12`; default enabled range is `1024-65535`; preserve custom TCP/UDP ranges. Requires strategy restart. |
| IPSet Filter | `:ipset_switch_status`, `:ipset_switch` | Read/write `lists/ipset-all.txt` and `.backup`. Actual states are `loaded` (real list), `none` (sentinel `203.0.113.113/32`), `any` (empty file). A missing backup prevents restoring loaded mode. |
| User lists | `:load_user_lists`, `lists/*-user.txt` | Create missing upstream-compatible user files and edit them using atomic replacement. Keep large upstream lists streamed/paged. |
| Tests/auto selection | `service.bat :run_tests`, `utils/test zapret.ps1`, `utils/targets.txt` | Adapter to the PowerShell test script, with progress output and cancellation. Its interactive prompts and global process termination require guarding before fully automated use. Never silently choose a strategy. |
| Diagnostics | `:service_diagnostics` | Read-only checks for files, BFE, WinDivert, proxies, path, conflicts, DNS, hosts, and services. Destructive repair actions from the batch script require separate explicit UI actions. |
| Update check | `:service_check_updates`, `.service/version.txt` | Separate Desktop and upstream release checks. Local version falls back to `LOCAL_VERSION` in `service.bat` when `.service/version.txt` is absent. |
| Replace active fakes | `:replace_active_fakes`, `bin/ACTIVE_*.bin` | Select only bundled `.bin` files and atomically replace the active payload; compare hashes for display. |
| IPSet/hosts updates | `:ipset_update`, `:hosts_update` | Download only from fixed upstream URLs. Validate and atomically replace IPSet; hosts update is review-only because it changes a system file. |
| Startup/update flags | `utils/check_updates.enabled`, service auto start | Desktop JSON settings control Desktop startup. `zapret` service startup is a separate Windows service setting. |

`Zapret.Core` defines models and boundaries. `Zapret.Infrastructure` owns file,
process, service, registry, privilege, HTTP, and script adapters.
`Zapret.Desktop` owns Avalonia views and view models. A distribution abstraction
locates the local upstream root and validates assets; UI never assumes a fixed
number of strategies.

## Important compatibility and safety constraints

- Batch strategies use `%BIN%`, `%LISTS%`, `%GameFilterTCP%`, `%GameFilterUDP%`,
  caret line continuation, and sometimes escaped `^!`. Parsing must preserve
  token boundaries and reject unrecognized variable expansions or commands.
- `service.bat` installs `zapret` using `sc create` with `start= auto`, then
  writes `HKLM\System\CurrentControlSet\Services\zapret\zapret-discord-youtube`.
  Removal also kills every `winws.exe` and removes WinDivert services; Desktop
  must scope destructive operations more narrowly and disclose that difference.
- The PowerShell tester mutates `ipset-all.txt`, launches BAT files, kills all
  `winws` instances, and waits for keyboard input. It cannot safely run beside
  an unrelated instance or active service. An adapter needs exclusive control
  and recovery of the IPSet backup after cancellation.
- Upstream `:service_diagnostics` can delete conflicting services and Discord
  caches after prompts. Desktop diagnostics should report findings without
  performing those actions.
- Do not bundle binary components in a release until Flowseal/bol-van MIT
  notices and WinDivert LGPLv3/GPLv2 obligations have been reviewed and included.

## Delivery order

1. Verify discovery, strict parser, argument construction, settings and state
   transitions with unit tests.
2. Implement process/service controllers and read-only diagnostics; test on
   Windows with elevation where required.
3. Wire the UI to those services, then add lists, filter controls, tests, tray,
   updates, and log export.
4. Validate both manual launch and service operation on a Windows 10/11 x64
   machine before claiming end-to-end support.
