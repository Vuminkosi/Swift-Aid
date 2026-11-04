CREATE ROLE p21_migration_owner
    LOGIN
    SUPERUSER
    PASSWORD 'swiftaid-local-fixture-p21-only';

CREATE DATABASE swiftaid_p21a
    OWNER p21_migration_owner;

CREATE ROLE swiftaid_identity_test
    LOGIN
    SUPERUSER
    PASSWORD 'swiftaid-local-fixture-identity-only';

CREATE DATABASE swiftaid_identity_test
    OWNER swiftaid_identity_test;

CREATE DATABASE swiftaid_intake_test
    OWNER p21_migration_owner;

CREATE ROLE p21_helper_owner WITH NOLOGIN NOINHERIT;
CREATE ROLE p21_lock_runtime WITH NOLOGIN NOINHERIT;
