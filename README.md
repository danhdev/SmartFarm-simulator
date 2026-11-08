# SmartFarm simulator workspace

This repository contains the Raspberry Pi/QEMU simulator, the SmartFarm backend,
and the separate frontend used for the local demo.

- [`qemu-raspbian/`](qemu-raspbian/README.md): QEMU setup, four Pi configurations,
  and CSV-to-MQTT replay code.
- [`MUGVN-SmartFarm/MUGVN-SmartFarm/`](MUGVN-SmartFarm/MUGVN-SmartFarm/README.md):
  .NET API, ingestion worker, Compose services, and Datadog configuration.
- [`SmartFarm-FE/`](SmartFarm-FE/README.md): Svelte frontend.

The source files from the existing nested Git repositories are included here.
Their separate local Git histories remain in their own `.git` directories and
are not part of this repository's history.

Local `.env` files, `qemu-raspbian/.env.mk`, downloaded datasets, VM images,
dependency folders, and build output are ignored. Obtain the AIRC-LABWSN dataset
separately and place it under `dataset/` when running the CSV replay. Copy
`MUGVN-SmartFarm/MUGVN-SmartFarm/.env.example` to `.env` and fill in local values
before starting the Compose stack.
