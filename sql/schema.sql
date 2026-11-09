CREATE SCHEMA tracking;

CREATE TABLE tracking.branches (
    id integer PRIMARY KEY CHECK (id > 0),
    name text NOT NULL CHECK (btrim(name) <> '')
);

CREATE TABLE tracking.shipments (
    id integer PRIMARY KEY CHECK (id > 0),
    last_branch_id integer NOT NULL REFERENCES tracking.branches(id),
    last_status text NOT NULL CHECK (last_status = btrim(last_status) AND length(last_status) BETWEEN 1 AND 32),
    updated_ms bigint NOT NULL CHECK (updated_ms >= 0),
    revision integer NOT NULL CHECK (revision BETWEEN 1 AND 64)
);

CREATE TABLE tracking.tracking_events (
    shipment_id integer NOT NULL REFERENCES tracking.shipments(id) ON DELETE CASCADE,
    sequence integer NOT NULL CHECK (sequence BETWEEN 1 AND 64),
    branch_id integer NOT NULL REFERENCES tracking.branches(id),
    status text NOT NULL CHECK (status = btrim(status) AND length(status) BETWEEN 1 AND 32),
    occurred_ms bigint NOT NULL CHECK (occurred_ms >= 0),
    PRIMARY KEY (shipment_id, sequence)
);

CREATE INDEX shipments_branch_updated_idx
    ON tracking.shipments (last_branch_id, updated_ms DESC, id ASC);
