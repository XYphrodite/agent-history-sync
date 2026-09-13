CREATE TABLE repositories (
    name text PRIMARY KEY CHECK (name ~ '^[a-z0-9-]{1,64}$'),
    manifest bytea NOT NULL CHECK (octet_length(manifest) BETWEEN 1 AND 65536),
    encrypted_index bytea NOT NULL CHECK (octet_length(encrypted_index) BETWEEN 32 AND 16777216),
    revision text NOT NULL CHECK (revision ~ '^[0-9a-f]{32}$'),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE blobs (
    repository_name text NOT NULL REFERENCES repositories(name),
    hash text NOT NULL CHECK (hash ~ '^[0-9a-f]{64}$'),
    ciphertext bytea NOT NULL CHECK (octet_length(ciphertext) BETWEEN 32 AND 104857600),
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (repository_name, hash)
);
CREATE TABLE object_refs (
    repository_name text NOT NULL REFERENCES repositories(name),
    object_id text NOT NULL CHECK (object_id ~ '^[0-9a-f]{64}$'),
    hash text NOT NULL,
    PRIMARY KEY (repository_name, object_id),
    FOREIGN KEY (repository_name, hash) REFERENCES blobs(repository_name, hash)
);
