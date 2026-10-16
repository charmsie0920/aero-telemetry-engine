# Local development database: the official Postgres 17 image plus the two
# extensions Supabase already ships, pg_partman and pg_cron (P1.2).
#
# The official image has neither, and the local database should behave like
# Supabase so that the same migrations run on both.
FROM postgres:18

# The official image already has the PostgreSQL apt repository configured, so
# both extensions install as ordinary packages. Removing the package lists
# afterwards keeps the image small.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        postgresql-17-partman \
        postgresql-17-cron \
    && rm -rf /var/lib/apt/lists/*
