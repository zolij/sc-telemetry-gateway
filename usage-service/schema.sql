CREATE TABLE IF NOT EXISTS activity (
    installation_id TEXT NOT NULL,
    day TEXT NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('app', 'collection')),
    version TEXT NOT NULL,
    observed_at INTEGER NOT NULL,
    PRIMARY KEY (installation_id, day, kind)
);

CREATE INDEX IF NOT EXISTS activity_observed_at ON activity (observed_at);
