# PoBox ML-Agents Documentation

This folder is the consolidated reference for every brain, rig, scene, and training run in PoBox. It is generated from the actual code and ONNX files; numbers come from the project, not from guesses.

## Quick links

| File | Audience | What it is |
|---|---|---|
| [creatures_dashboard.html](creatures_dashboard.html) | Everyone | Interactive dashboard with Executive / Technical toggle. Open in a browser. |
| [scenes_layout.html](scenes_layout.html) | Designers, scene builders | Every scene, every top-level GameObject, what each one does. |
| [onnx_summary.md](onnx_summary.md) | ML engineers, ops | Matrix A (models) and Matrix B (physics/actuators). |
| [reports/architecture.md](reports/architecture.md) | Engineers | Three-tier architecture reference. |
| [reports/runtime_pipeline.md](reports/runtime_pipeline.md) | Engineers | Three-tier pipeline reference (observation → joint drive). |
| [reports/observation_action.md](reports/observation_action.md) | Engineers | Three-tier tensor blueprint. |
| [reports/reward_curriculum.md](reports/reward_curriculum.md) | ML engineers | Three-tier reward & curriculum reference. |

## Diagram assets

| SVG | Topic |
|---|---|
| [assets/lifecycle-simple.svg](assets/lifecycle-simple.svg) | Conceptual per-tick loop |
| [assets/lifecycle-detailed.svg](assets/lifecycle-detailed.svg) | Tensors, buffers, execution split |
| [assets/tensor-simple.svg](assets/tensor-simple.svg) | Observation → action |
| [assets/tensor-detailed.svg](assets/tensor-detailed.svg) | Index-level layout + drive parameters |
| [assets/sensor-reward-simple.svg](assets/sensor-reward-simple.svg) | From sensors to reward credit |
| [assets/sensor-reward-detailed.svg](assets/sensor-reward-detailed.svg) | Per-component reward tree |
| [assets/hyperparam-simple.svg](assets/hyperparam-simple.svg) | PPO at a glance |
| [assets/hyperparam-detailed.svg](assets/hyperparam-detailed.svg) | Full hyperparameter matrix |

## Source Mermaid files

The diagrams are compiled from the raw `.mmd` files in [diagrams/](diagrams/). To recompile:

```bash
npx @mermaid-js/mermaid-cli -i diagrams/<name>.mmd -o assets/<name>.svg -b transparent
```

## How the tiers work

Every document is structured the same way:

1. **Tier 1 (Very Basic)** — 30-second read. Executive summary. No jargon.
2. **Tier 2 (Basic)** — High-level overview of the components and flows.
3. **Tier 3 (Complete)** — Granular implementation specs, telemetry, and references.

If you only have 30 seconds, read Tier 1. If you have 5 minutes, read Tier 1 + Tier 2. If you have 30 minutes, read all three.
