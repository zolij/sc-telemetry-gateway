"use strict";

const MfdPageTriggers = (() => {
  function validate(pages, knownEvents) {
    const assigned = new Set();
    for (const page of pages) {
      const events = page.trigger_events === undefined ? [] : page.trigger_events;
      if (!Array.isArray(events)) throw new Error("Page trigger events must be a list.");
      for (const event of events) {
        if (!knownEvents.includes(event)) throw new Error("Unknown page trigger event.");
        if (assigned.has(event)) throw new Error("An event can trigger only one page in a profile.");
        assigned.add(event);
      }
    }
  }

  function assign(pages, pageId, event, confirmMove) {
    const target = pages.find(page => page.id === pageId);
    if (!target) throw new Error("The target page no longer exists.");
    const owner = pages.find(page => page.trigger_events?.includes(event));
    if (owner?.id === pageId) return true;
    if (owner && !confirmMove(owner, target)) return false;
    if (owner) owner.trigger_events = owner.trigger_events.filter(item => item !== event);
    target.trigger_events = [...(target.trigger_events ?? []), event];
    return true;
  }

  function createCursor() {
    let stream = null, sequence = 0;
    return {
      reset() { stream = null; sequence = 0; },
      target(snapshot, pages, enabled) {
        const events = snapshot.events === undefined ? [] : snapshot.events;
        if (typeof snapshot.stream_id !== "string" || !snapshot.stream_id || !Array.isArray(events))
          throw new Error("Invalid event snapshot.");
        let previous = 0;
        for (const event of events) {
          if (!event || !Number.isSafeInteger(event.sequence) || event.sequence <= previous ||
              typeof event.type !== "string")
            throw new Error("Invalid event sequence.");
          previous = event.sequence;
        }
        if (stream === null) {
          stream = snapshot.stream_id;
          sequence = previous;
          return null;
        }
        if (stream !== snapshot.stream_id) { stream = snapshot.stream_id; sequence = 0; }
        const fresh = events.filter(event => event.sequence > sequence);
        sequence = Math.max(sequence, previous);
        if (!enabled) return null;
        let target = null;
        for (const event of fresh) {
          const page = pages.find(page => page.trigger_events?.includes(event.type));
          if (page) target = { page, event };
        }
        return target;
      }
    };
  }

  return { validate, assign, createCursor };
})();
