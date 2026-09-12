# ManimalTerminal

Backport of the retail Escape from Tarkov **Terminal** map into **SPT 4.1** —
scene restore, component recovery, and gameplay fixup, following the approach
proven on [ManimalIcebreaker](../ManimalIcebreaker).

- `terminal-client/` — BepInEx client plugin (`Manimal-Terminal`)
- `terminal-server/` — SPT server mod (binds the native Terminal location slot)
- `terminal-fika/` — optional Fika sync addon for playing and headless hosts;
  [installation and multiplayer test checklist](docs/FIKA-SYNC.md)
- `docs/` — the map-backport playbook + working-notes snapshots

Native integration work: [parity audit, recovered authoring and verification](docs/TERMINAL-PARITY.md).

Source code: [danauraborealis/ManimalTerminal](https://github.com/danauraborealis/ManimalTerminal).

Version **0.2.2** targets SPT 4.1; compiled against SPT 4.1.5.
The client, server, civilian prepatcher and optional Fika addon are included in
the solution. See [4.1 installation, build commands and verification](docs/SPT-4.1-PORT.md).

Extract the full `Manimal-Terminal-SPT-4.1-0.2.2.zip` over your SPT 4.1 game root.
For Fika, also install the matching Fika addon on every playing and headless client.
Install archives contain runtime files only. Builds do not deploy automatically.

For Forge, use GUID `com.manimal.terminal`, client name `Manimal-Terminal`,
and version `0.2.2`; the server declares the same GUID and version with name
`ManimalTerminal` and author `Manimal`. The optional client addon has its own
GUID `com.manimal.terminal.fika` and name `Manimal-TerminalFika`.
Publish the matching release source to the repository above and include that
link on the Forge listing. Upload the full compiled package for new installs;
the update and binaries archives require an existing installation.
