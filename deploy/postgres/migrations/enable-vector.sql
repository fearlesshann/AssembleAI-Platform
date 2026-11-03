-- Explicit knowledge-base initialization; core authorization and sessions do not depend on vector.
BEGIN;
SELECT pg_advisory_xact_lock(162109, 1);
DO $$
BEGIN
    IF current_database() <> 'assembleai_platform' THEN
        RAISE EXCEPTION 'Refusing platform migration in database %', current_database();
    END IF;
END $$;

CREATE EXTENSION IF NOT EXISTS vector VERSION '0.8.7';
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'vector' AND extversion = '0.8.7') THEN
        RAISE EXCEPTION 'Expected pgvector 0.8.7; review extension upgrade before migration';
    END IF;
END $$;
COMMIT;
