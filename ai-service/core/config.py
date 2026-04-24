from pydantic_settings import BaseSettings


class Settings(BaseSettings):
    database_url: str                  # postgres://user:pass@host:5432/db
    gcp_project_id: str                # e.g. taqrrerk
    gcs_bucket: str                    # taqreerk-uploads (me-central1, Doha)
    translate_location: str = "global" # Google Translate API location
    internal_api_key: str = ""         # optional: shared secret for .NET → Python calls

    model_config = {"env_file": ".env", "env_file_encoding": "utf-8"}


settings = Settings()  # type: ignore[call-arg]
