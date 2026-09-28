# Straight Train Stop

Cities: Skylines II code mod for long trains whose front bogies wander onto a crossover and bend into the neighbouring track while stopping at a station.

Select a station and open **Visual Customisation**. **Straight-only platforms** lists the station's platform tracks with a switch for each end, left and right as seen from the station's street side, plus one switch at the top for every end at once.

With an end switched on, the lane changes at that end of the track are removed and the straight track is kept, so the platform is reachable only straight on; the junction itself stays. Only lanes running into or out of the station's own tracks are touched: connections between two player-placed tracks are never changed, and metro and tram tracks are ignored.

It uses the game's own lane pipeline: the station's lanes are regenerated and the lane changes are discarded right after, the same way the game discards any lane it no longer needs. Switching an end off regenerates the station with its lane changes. The choices are saved per station and per track end.

Removed lane changes are not part of the save. If the mod is uninstalled, a station it changed stays without them until its lanes are regenerated, for example by upgrading or rebuilding the station; switching every station off first avoids this.

Version 1.0.0 targets Cities: Skylines II 1.6.*.

[Paradox Mods](https://mods.paradoxplaza.com/mods/161180/Windows) · [Forum thread](https://forum.paradoxplaza.com/forum/threads/straight-train-stop-mod.1943001/) · MIT licence, see [LICENSE](LICENSE).
