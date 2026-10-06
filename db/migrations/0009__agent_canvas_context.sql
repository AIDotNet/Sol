-- 0009__agent_canvas_context.sql
-- Per-run canvas snapshot attached by the executor browser at send time.
--
-- The Agent server has no canvas state of its own; the browser that starts a run is the one that
-- holds the live graph. This column carries its compact context text (current selection, node
-- overview) so the runner can place it before the model without a probe read_canvas round trip.
-- Plain text on purpose: the client formats it, the server only bounds its size.

ALTER TABLE agent_run ADD COLUMN IF NOT EXISTS canvas_context_json text NULL;
