/*!
 * Cities: Skylines II UI Module
 *
 * Id: StraightTrainStop
 * Author: Fabiozsche
 * Version: 1.0.3
 * Dependencies:
 */

const GROUP = "straightTrainStop";
const SECTIONS = "game-ui/game/components/selected-info-panel/selected-info-sections/selected-info-sections.tsx";
const VISUAL_CUSTOMISATION = "Game.UI.InGame.VisualCustomizeSection";
// Our own info section, shown instead on stations that have no Visual Customisation.
const OWN_SECTION = "StraightTrainStop.StationTracksSection";

const CHECKBOX = "game-ui/common/input/toggle/checkbox/checkbox.tsx";
const INFO_ROW = "game-ui/game/components/selected-info-panel/shared-components/info-row/info-row.tsx";
const INFO_SECTION = "game-ui/game/components/selected-info-panel/shared-components/info-section/info-section.tsx";

const React = window.React;
const api = window["cs2/api"];
const h = React.createElement;

const stationSelected$ = api.bindValue(GROUP, "stationSelected", false);
const tracks$ = api.bindValue(GROUP, "tracks", "[]");
const status$ = api.bindValue(GROUP, "junctionStatus", "");
const tramStop$ = api.bindValue(GROUP, "tramStop", false);
const tramLocked$ = api.bindValue(GROUP, "tramLocked", false);

// The game's own panel pieces, the ones the Historical row uses, looked up when the module
// registers. Where any is missing the panel falls back to the card below, so it never vanishes.
const vanilla = { Checkbox: null, InfoRow: null, InfoSection: null };

function lookup(moduleRegistry, path, name) {
  try {
    return moduleRegistry.get(path, name) || null;
  } catch (e) {
    return null;
  }
}

const endsStyle = { display: "flex", flexDirection: "row", alignItems: "center" };
const endCellStyle = { width: "64rem", display: "flex", justifyContent: "center" };
const endHeadingStyle = { width: "64rem", textAlign: "center", fontSize: "11rem", opacity: 0.6 };

// Fallback card. Same inset and width as the game's palette box above it in Visual Customisation.
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

// The cells for a track's two ends, or two headings: one per column the kind uses.
function Ends({ columns, cells }) {
  return h(
    "div",
    { style: endsStyle },
    columns[0] ? cells[0] : null,
    columns[1] ? cells[1] : null
  );
}

function TrackRow({ index, label, track, columns }) {
  if (vanilla.InfoRow) {
    const cell = (open, checked, side) =>
      h("div", { style: endCellStyle }, open ? h(vanilla.Checkbox, { checked: !!checked, onChange: (value) => setSide(index, side, value) }) : null);
    return h(vanilla.InfoRow, {
      subRow: true,
      noShrinkRight: true,
      left: label,
      right: h(Ends, { columns, cells: [cell(track.lo, track.l, 0), cell(track.ro, track.r, 1)] }),
    });
  }
  return h(
    "div",
    { style: rowStyle },
    h("div", { style: rowLabelStyle }, label),
    columns[0] ? h(EndCell, { open: track.lo, active: track.l, onClick: () => setSide(index, 0, !track.l) }) : null,
    columns[1] ? h(EndCell, { open: track.ro, active: track.r, onClick: () => setSide(index, 1, !track.r) }) : null
  );
}

// One kind of track: its column headings, then a row per track. A column where every track of
// the kind stops, as at a terminal's street end, is left out.
function TrackGroup({ items, mixed }) {
  const metro = !!items[0].track.m;
  const facesStreet = !!items[0].track.fs;
  const columns = [items.some((item) => item.track.lo), items.some((item) => item.track.ro)];
  const heading = mixed ? (metro ? "Metro" : "Railway") : "";
  const headings = [facesStreet ? "Far end" : "Left end", facesStreet ? "Street end" : "Right end"];
  const headingRow = vanilla.InfoRow
    ? h(vanilla.InfoRow, {
        subRow: true,
        noShrinkRight: true,
        left: heading,
        right: h(Ends, { columns, cells: headings.map((text) => h("div", { style: endHeadingStyle }, text)) }),
      })
    : h(
        "div",
        { style: rowStyle },
        h("div", { style: rowLabelStyle }, heading),
        columns[0] ? h("div", { style: columnHeaderStyle }, headings[0]) : null,
        columns[1] ? h("div", { style: columnHeaderStyle }, headings[1]) : null
      );
  return h(
    React.Fragment,
    null,
    headingRow,
    items.map((item, position) =>
      h(TrackRow, {
        key: item.index,
        index: item.index,
        label: (metro ? "Metro " : "Track ") + (position + 1),
        track: item.track,
        columns,
      })
    )
  );
}

// A tram stop: one switch for the crossings at the road nodes either side of it.
function TramStopControl({ locked, status }) {
  const label = "Straight tram tracks";
  if (vanilla.InfoRow && vanilla.Checkbox && vanilla.InfoSection) {
    return h(
      vanilla.InfoSection,
      { disableFocus: true },
      h(vanilla.InfoRow, {
        left: label,
        right: h(vanilla.Checkbox, { checked: !!locked, onChange: (value) => send("setTramStop", value) }),
      }),
      status ? h(vanilla.InfoRow, { subRow: true, subRowDimmed: true, left: status }) : null
    );
  }
  return h(
    "div",
    { style: cardStyle },
    h(
      "div",
      { style: headerStyle },
      h("div", { style: titleStyle }, label),
      h(Toggle, { label: locked ? "ON" : "OFF", active: !!locked, onClick: () => send("setTramStop", !locked) })
    ),
    status ? h("div", { style: statusStyle }, status) : null
  );
}

function StationJunctionControl() {
  const stationSelected = api.useValue(stationSelected$);
  const tracksJson = api.useValue(tracks$);
  const status = api.useValue(status$);
  const tramStop = api.useValue(tramStop$);
  const tramLocked = api.useValue(tramLocked$);

  if (tramStop) {
    return h(TramStopControl, { locked: tramLocked, status });
  }
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
  const allOn = total > 0 && on === total;

  // Railway tracks come first, then metro tracks; each kind gets its own column headings.
  const groups = [false, true]
    .map((metro) => tracks.map((track, index) => ({ track, index })).filter((item) => !!item.track.m === metro))
    .filter((items) => items.length > 0);
  const mixed = groups.length > 1;
  const rows = total > 0 ? groups.map((items) => h(TrackGroup, { key: items[0].index, items, mixed })) : null;

  if (vanilla.InfoRow && vanilla.Checkbox && vanilla.InfoSection) {
    // The master box shows the game's mixed state while only some ends are on.
    const master = total > 0
      ? h(vanilla.Checkbox, {
          multistate: true,
          checked: on === 0 ? false : allOn ? true : undefined,
          onMultistateChange: () => send("setAllTracks", !allOn),
        })
      : null;
    return h(
      vanilla.InfoSection,
      { disableFocus: true },
      h(vanilla.InfoRow, { left: "Straight-only platforms", right: master }),
      rows,
      status ? h(vanilla.InfoRow, { subRow: true, subRowDimmed: true, left: status }) : null
    );
  }

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
    rows,
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
  vanilla.Checkbox = lookup(moduleRegistry, CHECKBOX, "Checkbox");
  vanilla.InfoRow = lookup(moduleRegistry, INFO_ROW, "InfoRow");
  vanilla.InfoSection = lookup(moduleRegistry, INFO_SECTION, "InfoSection");
  if (!(vanilla.Checkbox && vanilla.InfoRow && vanilla.InfoSection)) {
    vanilla.Checkbox = vanilla.InfoRow = vanilla.InfoSection = null;
  }
  moduleRegistry.extend(SECTIONS, "selectedInfoSectionComponents", wrapVisualCustomisation);
}
