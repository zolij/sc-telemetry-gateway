"use strict";

const MfdPresentation = (() => {
  function readable(text) {
    return text.replace(/^(VEHICLE_COMPONENT_DEPLOYMENT_STATE|VEHICLE_MASTER_MODE|ACTOR_LOCOMOTION_STATE|ACTOR_CREW_ROLE|DOOR_OPEN_STATE|DOOR_LOCK_STATE|ANNUNCIATOR_STATE|VEHICLE_TYPE|COUNTERMEASURE_TYPE)_/, "")
      .replace(/_/g, " ").toLowerCase().replace(/\b\w/g, letter => letter.toUpperCase());
  }

  function validMaximum(maximum) {
    return typeof maximum === "number" && Number.isFinite(maximum) && maximum > 0;
  }

  function format(value, decimals, mode = "number", maximum = null) {
    if (!["number", "percent_fraction", "percent", "percent_max"].includes(mode))
      throw new Error("Unsupported display format.");
    if (value === null || value === undefined) return "Unavailable";
    if (Array.isArray(value)) return value.length ? value.map(item => format(item, decimals, mode, maximum)).join(" / ") : "None";
    if (typeof value === "number") {
      if (!Number.isFinite(value)) return "Unavailable";
      if (mode === "percent_max" && !validMaximum(maximum)) return "Unavailable";
      const scaled = mode === "percent" ? value / 100 : mode === "percent_max" ? value / maximum : value;
      if (!Number.isFinite(scaled)) return "Unavailable";
      return new Intl.NumberFormat(undefined, {
        style: mode === "number" ? "decimal" : "percent",
        maximumFractionDigits: decimals
      }).format(scaled);
    }
    if (typeof value === "boolean") return value ? "Yes" : "No";
    if (typeof value === "string") return value ? (/^[A-Z][A-Z0-9_]*$/.test(value) ? readable(value) : value) : "Unavailable";
    return "Unavailable";
  }

  return { readable, format, validMaximum };
})();
