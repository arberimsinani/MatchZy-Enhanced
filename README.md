<div align="center">

  <img src="assets/icon.svg" alt="Matchzy Enhanced" width="140" height="140">

# Matchzy Enhanced

⚡ **Enhanced CS2 match management plugin tailored for tournament automation**

  <p>Enhanced fork of MatchZy tailored for the automatic tournament platform. Adds more events and enables external tools to setup, control, and track matches in real-time.</p>

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![C#](https://img.shields.io/badge/C%23-239120?logo=c-sharp&logoColor=white)](https://docs.microsoft.com/en-us/dotnet/csharp/)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)

**🔗 [MatchZy Auto Tournament](https://github.com/sivert-io/matchzy-auto-tournament)** • **[CS2 Server Manager](https://github.com/sivert-io/cs2-server-manager)**

</div>

---

## 🚀 Quick Start

**Use [CS2 Server Manager](https://github.com/sivert-io/cs2-server-manager)** for automated setup with MatchZy Enhanced pre-configured:

👉 **[Get Started with CS2 Server Manager](https://github.com/sivert-io/cs2-server-manager)**

### Manual Installation

1. Download the [latest release](https://github.com/sivert-io/MatchZy-Enhanced/releases)
2. Extract to `game/csgo/` directory
3. Restart your server

📖 **[Documentation](https://docs.sivert.io/docs/me)**

---

## ✨ What's Enhanced

Built for **[MatchZy Auto Tournament](https://github.com/sivert-io/matchzy-auto-tournament)** with extended APIs and events for tournament automation:

### Tournament Features
- 📡 **Extended event system** for real-time match tracking
- 🔧 **Match report API** with structured JSON state
- 🔄 **Thread-safe operations** for reliable automation
- 🤖 **Simulation mode** for testing and demos
- 🔁 **Event retry system** with automatic queue and recovery
- 📊 **Server tracking** with health monitoring and status events
- 💾 **Pull API** for direct match stats retrieval

### Player Features
- 🚀 **Auto-ready system** — Instant match starts (optional)
- ⏸️ **Enhanced pauses** — Team limits, timeouts, dual unpause
- ⏱️ **Side selection timer** — Auto-decide after knife round
- 🏳️ **`.gg` command** — Team vote to forfeit early
- 🚫 **FFW system** — Handle full team disconnects
- ⚡ **Smart demo delays** — 10s restart when demos disabled
- 📺 **Center notifications** — Important events shown center-screen with countdown timers

---

## 📖 Documentation (docs.sivert.io)

- 📋 **[Configuration Guide](https://docs.sivert.io/docs/me/user/configuration)** — All ConVars and examples
- 🎮 **[Commands Reference](https://docs.sivert.io/docs/me/user/commands)** — Player and admin commands
- 🔗 **[Integration Guide](https://docs.sivert.io/docs/me/advanced/integration)** — API endpoints and events
- 📝 **[Changelog](https://docs.sivert.io/docs/me/advanced/changelog)** — Release history

---

## 🔗 Related Projects

- **[MatchZy Auto Tournament](https://github.com/sivert-io/matchzy-auto-tournament)** — Automated tournament platform
- **[CS2 Server Manager](https://github.com/sivert-io/cs2-server-manager)** — Multi-server deployment tool

## 🙏 Credits

**Original MatchZy:** [shobhit-pathak/MatchZy](https://github.com/shobhit-pathak/MatchZy) by WD-  
**Enhanced Fork:** Maintained by [sivert-io](https://github.com/sivert-io) for [MatchZy Auto Tournament](https://github.com/sivert-io/matchzy-auto-tournament)

Built with [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp/) • Inspired by [Get5](https://github.com/splewis/get5)

---

<div align="center">

<strong>Made with ❤️ for the CS2 community</strong>

</div>

## Changes in this fork

This is [cs2-xpbot](https://github.com/arberimsinani/cs2-xpbot)'s fork of
MatchZy-Enhanced. It tracks upstream `dev`; the branch `xpbot` carries the
changes below on top, each written to be offered upstream.

- **The game can no longer change map before MatchZy does.** `HandleMatchEnd`
  raises `mp_match_restart_delay` to cover the delay it chose in every branch,
  not only when a demo upload URL is configured. Before this, a server
  recording without an upload URL waited `tv_delay + 25` s against the game's
  25 s default, and the game loaded the next map of the map group first, with
  the demo still being written.
- **`css_forcewin <team1|team2>`** ends the map being played with the given
  team as its winner and lets the normal match-end path run: series score,
  clinch, demo and map change. `css_endmatch` drops the whole loaded series;
  this ends one map of it. Admin or console/RCON only; needs a live map.
