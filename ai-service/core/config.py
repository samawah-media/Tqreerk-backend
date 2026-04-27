from pydantic_settings import BaseSettings


# Maps .NET / Npgsql connection-string keys to libpq keys.
# Used so the Python service can accept the SAME DATABASE_URL secret the .NET API uses.
_DOTNET_TO_LIBPQ = {
    "host":            "host",
    "server":          "host",
    "port":            "port",
    "database":        "dbname",
    "username":        "user",
    "user id":         "user",
    "password":        "password",
    "ssl mode":        "sslmode",
    "sslmode":         "sslmode",
    "search path":     "options",  # rarely used, mapped if present
}


def _normalize_database_url(value: str) -> str:
    """Accept either a libpq URI (postgres://...) / keyword string, or a .NET
    Npgsql connection string (Host=...;Database=...;Username=...;Password=...).

    .NET strings get converted into libpq keyword format which psycopg understands.
    """
    v = value.strip()
    if v.startswith(("postgres://", "postgresql://")):
        return v
    if "=" in v and ";" in v:  # looks like a .NET connection string
        parts = []
        for chunk in v.split(";"):
            if "=" not in chunk:
                continue
            k, _, val = chunk.partition("=")
            key = _DOTNET_TO_LIBPQ.get(k.strip().lower())
            if not key:
                continue
            val = val.strip()
            # quote values containing spaces or special chars
            if any(c in val for c in " '\\"):
                val = "'" + val.replace("\\", "\\\\").replace("'", "\\'") + "'"
            parts.append(f"{key}={val}")
        return " ".join(parts)
    return v


class Settings(BaseSettings):
    database_url: str                  # libpq URI, libpq keyword, OR .NET-style — auto-normalized
    gcp_project_id: str                # e.g. taqrrerk
    gcs_bucket: str                    # taqreerk-uploads (me-central1, Doha)
    translate_location: str = "global" # Google Translate API location
    internal_api_key: str = ""         # optional: shared secret for .NET → Python calls

    model_config = {"env_file": ".env", "env_file_encoding": "utf-8"}

    def model_post_init(self, __context) -> None:
        self.database_url = _normalize_database_url(self.database_url)


settings = Settings()  # type: ignore[call-arg]
