/*!
 * Cities: Skylines II UI Module
 *
 * Id: StraightTrainStop
 * Author: Fabiozsche
 * Version: 1.0.1
 * Dependencies:
 */

const GROUP = "straightTrainStop";
const SECTIONS = "game-ui/game/components/selected-info-panel/selected-info-sections/selected-info-sections.tsx";
const VISUAL_CUSTOMISATION = "Game.UI.InGame.VisualCustomizeSection";
// Our own info section, shown instead on stations that have no Visual Customisation.
const OWN_SECTION = "StraightTrainStop.StationTracksSection";

const React = window.React;
const api = window["cs2/api"];
const h = React.createElement;

const stationSelected$ = api.bindValue(GROUP, "stationSelected", false);
const tracks$ = api.bindValue(GROUP, "tracks", "[]");
const status$ = api.bindValue(GROUP, "junctionStatus", "");
const endsFaceStreet$ = api.bindValue(GROUP, "endsFaceStreet", false);

// Same inset and width as the game's palette box above it in Visual Customisation.
const cardStyle = {
  margin: "10rem 21rem 10rem 21rem",
  padding: "10rem 12rem",
  width: "358rem",
  boxSizing: "border-box",
  overflow: "hidden",
  backgroundColor: "rgba(9, 20, 32, 0.34)",
  borderRadius: "4rem",
};
const headerStyle = {
  display: "flex",
  flexDirection: "row",
  alignItems: "center",
  width: "334rem",
};
const titleStyle = {
  width: "270rem",
  flexGrow: 0,
  flexShrink: 0,
  color: "#ffffff",
  fontSize: "14rem",
  fontWeight: "bold",
};
const statusStyle = {
  width: "334rem",
  marginTop: "7rem",
  color: "#b9e0ff",
  fontSize: "11.5rem",
  lineHeight: "15rem",
};
const rowStyle = {
  display: "flex",
  flexDirection: "row",
  alignItems: "center",
  width: "334rem",
  marginTop: "6rem",
};
const rowLabelStyle = {
  width: "182rem",
  flexGrow: 0,
  flexShrink: 0,
  color: "#ffffff",
  fontSize: "12rem",
};
const columnHeaderStyle = {
  width: "72rem",
  marginLeft: "4rem",
  textAlign: "center",
  color: "rgba(255, 255, 255, 0.6)",
  fontSize: "11rem",
};
const cellStyle = {
  width: "72rem",
  marginLeft: "4rem",
  display: "flex",
  justifyContent: "center",
};

function send(name, value) {
  try {
    api.trigger(GROUP, name, value);
  } catch (e) {
    // The managed side can be unavailable for a frame while a city is loading.
  }
}

function setSide(row, side, enabled) {
  send("setTrackSide", row * 4 + side * 2 + (enabled ? 1 : 0));
}

function parseTracks(json) {
  try {
    const tracks = JSON.parse(json);
    return Array.isArray(tracks) ? tracks : [];
  } catch (e) {
    return [];
  }
}

function Toggle({ label, active, onClick }) {
  const [hover, setHover] = React.useState(false);
  const background = active
    ? (hover ? "#38b7eb" : "#249fd2")
    : (hover ? "rgba(255, 255, 255, 0.26)" : "rgba(255, 255, 255, 0.16)");
  const style = {
    width: "64rem",
    height: "26rem",
    display: "flex",
    alignItems: "center",
    justifyContent: "center",
    flexShrink: 0,
    borderRadius: "13rem",
    color: "#ffffff",
    backgroundColor: background,
    fontSize: "11rem",
    fontWeight: "bold",
    cursor: "pointer",
  };
  return h(
    "div",
    {
      style,
      onClick,
      onMouseEnter: () => setHover(true),
      onMouseLeave: () => setHover(false),
    },
    label
  );
}

// A cell for one end: its switch, or an empty space where the track stops there.
function EndCell({ open, active, onClick }) {
  return h("div", { style: cellStyle }, open ? h(Toggle, { label: active ? "ON" : "OFF", active, onClick }) : null);
}

function TrackRow({ index, track, columns }) {
  return h(
    "div",
    { style: rowStyle },
    h("div", { style: rowLabelStyle }, "Track " + (index + 1)),
    columns[0] ? h(EndCell, { open: track.lo, active: track.l, onClick: () => setSide(index, 0, !track.l) }) : null,
    columns[1] ? h(EndCell, { open: track.ro, active: track.r, onClick: () => setSide(index, 1, !track.r) }) : null
  );
}

function StationJunctionControl() {
  const stationSelected = api.useValue(stationSelected$);
  const tracksJson = api.useValue(tracks$);
  const status = api.useValue(status$);
  const endsFaceStreet = api.useValue(endsFaceStreet$);

  if (!stationSelected) {
    return null;
  }

  const tracks = parseTracks(tracksJson);
  let on = 0;
  let total = 0;
  tracks.forEach((track) => {
    if (track.lo) total++;
    if (track.ro) total++;
    if (track.lo && track.l) on++;
    if (track.ro && track.r) on++;
  });
  // A column where every track stops, as at a terminal's street end, is left out.
  const columns = [tracks.some((track) => track.lo), tracks.some((track) => track.ro)];
  const allOn = total > 0 && on === total;
  const masterLabel = on === 0 ? "OFF" : allOn ? "ON" : "MIXED";

  return h(
    "div",
    { style: cardStyle },
    h(
      "div",
      { style: headerStyle },
      h("div", { style: titleStyle }, "Straight-only platforms"),
      total > 0 ? h(Toggle, { label: masterLabel, active: on > 0, onClick: () => send("setAllTracks", !allOn) }) : null
    ),
    total > 0
      ? h(
          "div",
          { style: rowStyle },
          h("div", { style: rowLabelStyle }, ""),
          columns[0] ? h("div", { style: columnHeaderStyle }, endsFaceStreet ? "Far end" : "Left end") : null,
          columns[1] ? h("div", { style: columnHeaderStyle }, endsFaceStreet ? "Street end" : "Right end") : null
        )
      : null,
    total > 0 ? tracks.map((track, index) => h(TrackRow, { key: index, index, track, columns })) : null,
    status ? h("div", { style: statusStyle }, status) : null
  );
}

function wrapVisualCustomisation(components) {
  const Original = components && components[VISUAL_CUSTOMISATION];
  if (!Original) {
    return components;
  }

  function StraightTrainStopVisualCustomisation(props) {
    return h(
      React.Fragment,
      null,
      h(Original, props),
      h(StationJunctionControl)
    );
  }

  return {
    ...components,
    [VISUAL_CUSTOMISATION]: StraightTrainStopVisualCustomisation,
    [OWN_SECTION]: StationJunctionControl,
  };
}

export default function register(moduleRegistry) {
  moduleRegistry.extend(SECTIONS, "selectedInfoSectionComponents", wrapVisualCustomisation);
}
