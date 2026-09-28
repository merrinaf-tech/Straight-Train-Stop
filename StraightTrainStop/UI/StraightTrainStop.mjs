/*!
 * Cities: Skylines II UI Module
 *
 * Id: StraightTrainStop
 * Author: Fabiozsche
 * Version: 1.0.0
 * Dependencies:
 */

const GROUP = "straightTrainStop";
const SECTIONS = "game-ui/game/components/selected-info-panel/selected-info-sections/selected-info-sections.tsx";
const VISUAL_CUSTOMISATION = "Game.UI.InGame.VisualCustomizeSection";

const React = window.React;
const api = window["cs2/api"];
const h = React.createElement;

const stationSelected$ = api.bindValue(GROUP, "stationSelected", false);
const tracks$ = api.bindValue(GROUP, "tracks", "[]");
const removedCount$ = api.bindValue(GROUP, "removedCount", 0);
const status$ = api.bindValue(GROUP, "junctionStatus", "");

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
const copyStyle = { width: "270rem", flexGrow: 0, flexShrink: 0, paddingRight: "12rem", boxSizing: "border-box" };
const titleStyle = { color: "#ffffff", fontSize: "14rem", fontWeight: "bold" };
const descriptionStyle = {
  width: "258rem",
  marginTop: "3rem",
  color: "rgba(255, 255, 255, 0.68)",
  fontSize: "11.5rem",
  lineHeight: "15rem",
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
const rowHintStyle = { color: "rgba(255, 255, 255, 0.5)", fontSize: "11rem" };
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

function TrackRow({ index, track }) {
  const changes = (track.lc || 0) + (track.rc || 0);
  return h(
    "div",
    { style: rowStyle },
    h(
      "div",
      { style: rowLabelStyle },
      "Track " + (index + 1),
      changes > 0 ? h("span", { style: rowHintStyle }, "  " + changes + " lane change" + (changes === 1 ? "" : "s")) : null
    ),
    h(
      "div",
      { style: cellStyle },
      h(Toggle, { label: track.l ? "ON" : "OFF", active: track.l, onClick: () => setSide(index, 0, !track.l) })
    ),
    h(
      "div",
      { style: cellStyle },
      h(Toggle, { label: track.r ? "ON" : "OFF", active: track.r, onClick: () => setSide(index, 1, !track.r) })
    )
  );
}

function StationJunctionControl() {
  const stationSelected = api.useValue(stationSelected$);
  const tracksJson = api.useValue(tracks$);
  const removedCount = api.useValue(removedCount$);
  const status = api.useValue(status$);

  if (!stationSelected) {
    return null;
  }

  const tracks = parseTracks(tracksJson);
  let on = 0;
  tracks.forEach((track) => {
    if (track.l) on++;
    if (track.r) on++;
  });
  const total = tracks.length * 2;
  const allOn = total > 0 && on === total;
  const masterLabel = on === 0 ? "OFF" : allOn ? "ON" : "MIXED";
  const progress = removedCount > 0 ? " (" + removedCount + " removed)" : "";

  return h(
    "div",
    { style: cardStyle },
    h(
      "div",
      { style: headerStyle },
      h(
        "div",
        { style: copyStyle },
        h("div", { style: titleStyle }, "Straight-only platforms" + progress),
        h(
          "div",
          { style: descriptionStyle },
          "Removes the lane changes at each end of a platform track, so trains reach it only straight on. The top switch sets every end at once."
        )
      ),
      h(Toggle, { label: masterLabel, active: on > 0, onClick: () => send("setAllTracks", !allOn) })
    ),
    tracks.length > 0
      ? h(
          "div",
          { style: rowStyle },
          h("div", { style: rowLabelStyle }, ""),
          h("div", { style: columnHeaderStyle }, "Left end"),
          h("div", { style: columnHeaderStyle }, "Right end")
        )
      : null,
    tracks.map((track, index) => h(TrackRow, { key: index, index, track })),
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

  return { ...components, [VISUAL_CUSTOMISATION]: StraightTrainStopVisualCustomisation };
}

export default function register(moduleRegistry) {
  moduleRegistry.extend(SECTIONS, "selectedInfoSectionComponents", wrapVisualCustomisation);
}
