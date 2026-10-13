# Straight Train Stop

Cities: Skylines II code mod for long trains whose front bogies wander onto a crossover and bend into the neighbouring track while stopping at a station.

Select a railway station and open **Visual Customisation**, or look in the info panel for stations that have none. **Straight-only platforms** lists the station's platform tracks with a switch for each end, left and right as seen from the station's street side (far and street end where the platforms point at the street), plus one switch at the top for every end at once. An end where the track stops, as at a terminal's buffer stops, has no switch.

With an end switched on, the lane changes at that end of the track are removed and the straight track is kept, so the platform is reachable only straight on; the junction itself stays. Only lanes running into or out of the station's own tracks are touched: connections between two player-placed tracks are never changed. Metro stations work the same way, with their tracks listed apart from the railway ones in a mixed station.

A tram stop has one switch, **Straight tram tracks**, which removes the tram crossings at the two road nodes either side of it. This is the one place the mod changes junctions of roads the player built, and only on the stops where it is switched on: only tram lanes that change track, never a tram turning into another road or anything cars use.

It uses the game's own lane pipeline: the station's lanes are regenerated and the lane changes are discarded right after, the same way the game discards any lane it no longer needs. Switching an end off regenerates the station with its lane changes. The choices are saved per station and per track end.

Removed lane changes are not part of the save. If the mod is uninstalled, a station it changed stays without them until its lanes are regenerated, for example by upgrading or rebuilding the station; switching every station off first avoids this.

Version 1.0.4 targets Cities: Skylines II 1.6.*.

[Paradox Mods](https://mods.paradoxplaza.com/mods/161180/Windows) · [Forum thread](https://forum.paradoxplaza.com/forum/threads/straight-train-stop-mod.1943001/) · MIT licence, see [LICENSE](LICENSE).
