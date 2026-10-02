"""Watch a boxing policy in MuJoCo's own viewer, or print a strip of stills from it.

    python view_box.py --run spar                      live viewer on the newest checkpoint of a run;
                                                       reloads by itself as training saves new ones
    python view_box.py --ckpt checkpoints/bag/latest.pt --sheet logs/bag.png     a 4 x 4 contact sheet

House rule: show the simulator's own picture, during training and after, not just reward curves.

The policy is run through the same environment class the trainer uses, with one world, so what it sees
here is exactly what it was trained on; the state is copied into an ordinary MuJoCo data object each step
purely to be drawn. It shares the GPU with a training run without getting in its way.
"""
from __future__ import annotations

import argparse
import glob
import os
import sys
import time

import mujoco
import numpy as np
import torch

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from envs.boxing import BoxingEnv  # noqa: E402
from envs.getup import GetUpEnv  # noqa: E402
from ppo import PPO, PPOConfig  # noqa: E402


class Player:
    """Runs one world with the policies from one or two checkpoints.

    One checkpoint drives everybody in the model. Two drive a match between two different fighters, the
    first checkpoint the first fighter; they need not have been trained together, so two fighters fresh
    off the bag can be put in the ring by passing the match model with --xml.
    """

    def __init__(self, ckpts, xml: str = "", seed: int = 0, deterministic: bool = True, device: str = "cpu"):
        ckpts = [ckpts] if isinstance(ckpts, str) else list(ckpts)
        extra = torch.load(ckpts[0], map_location="cpu", weights_only=False).get("extra", {})
        self.xml = xml or extra.get("xml", "")
        if not os.path.exists(self.xml):
            raise SystemExit(f"model file not found: {self.xml!r}. Pass --xml.")
        if extra.get("mode", "") == "getup":
            # No helping hand: what is watched is what the game will get.
            self.env = GetUpEnv(self.xml, 1, device=device, seed=seed, cuda_graph=False, obs_noise=0.0, assist=0.0,
                                action_scale=float(extra.get("action_scale", 0.5)))
        else:
            self.env = BoxingEnv(self.xml, 1, device=device, seed=seed, cuda_graph=False, obs_noise=0.0, push_vel=0.0,
                                 action_scale=float(extra.get("action_scale", 0.5)), **extra.get("env", {}))
        self.split = len(ckpts) == 2 and self.env.K == 2
        self.ppos = [PPO(self.env.obs_dim, self.env.A, 1 if self.split else self.env.K, device, PPOConfig()) for _ in ckpts[: 2 if self.split else 1]]
        self.deterministic = deterministic
        self.m = self.env.m
        self.d = mujoco.MjData(self.m)
        self.load(ckpts)
        self.obs = self.env.reset()
        self.env.step_count.zero_()
        self.sync()

    def load(self, ckpts) -> None:
        ckpts = [ckpts] if isinstance(ckpts, str) else list(ckpts)
        for ppo, path in zip(self.ppos, ckpts):
            extra = ppo.load(path)
        self.ckpt = ckpts
        self.iters = int(extra.get("iter", 0))
        self.stage = extra.get("mode", "")

    def _act(self, ppo, obs):
        x = ppo.obs_rms.normalize(obs, ppo.cfg.obs_clip)
        return ppo.model.actor(x) if self.deterministic else ppo.model.dist(x).sample()

    def sync(self) -> None:
        self.d.qpos[:] = self.env.qpos[0].detach().cpu().numpy()
        self.d.qvel[:] = self.env.qvel[0].detach().cpu().numpy()
        mujoco.mj_forward(self.m, self.d)

    @torch.no_grad()
    def step(self) -> bool:
        """One control step. Returns True if the episode ended (somebody fell, or time ran out)."""
        if self.split:
            act = torch.cat([self._act(self.ppos[k], self.obs[k:k + 1]) for k in range(2)], 0)
        else:
            act = self._act(self.ppos[0], self.obs)
        self.obs, _, done, _ = self.env.step(act)
        self.sync()
        return bool(done.any().item())

    def focus(self) -> np.ndarray:
        """The point half way between the fighter and what it is fighting, at chest height."""
        return self.framing()[0]

    def framing(self):
        """(look-at point, camera azimuth in degrees) for a side-on view: the camera stands square to
        the line between the fighter and its target, so neither hides the other."""
        ids = [mujoco.mj_name2id(self.m, mujoco.mjtObj.mjOBJ_BODY, p + "pelvis") for p in ("a_", "b_")[: self.env.K]]
        a = np.array(self.d.xpos[ids[0]])
        b = np.array(self.d.xpos[ids[1]]) if self.env.K == 2 else np.array([0.0, 0.0, a[2]])   # the bag hangs at the origin
        c = 0.5 * (a + b)
        c[2] = 1.0
        bearing = float(np.degrees(np.arctan2(b[1] - a[1], b[0] - a[0])))
        return c, bearing + 90.0


def newest(run: str):
    """The newest save of a run: one file, or one per fighter for a match (model_000100_matt.pt, ..._zombie.pt)."""
    files = sorted(glob.glob(os.path.join(HERE, "checkpoints", run, "model_*.pt")))
    if not files:
        raise SystemExit(f"no checkpoints yet in checkpoints/{run}")
    stamp = os.path.basename(files[-1])[6:12]
    last = [f for f in files if os.path.basename(f)[6:12] == stamp]
    if len(last) == 1:
        return last
    # In the model's order, which the checkpoints record.
    order = torch.load(last[0], map_location="cpu", weights_only=False).get("extra", {}).get("fighters", [])
    return sorted(last, key=lambda f: order.index(os.path.basename(f)[13:-3]) if os.path.basename(f)[13:-3] in order else 99)


def sheet(player: Player, path: str, seconds: float, cols: int = 4, rows: int = 4) -> None:
    from PIL import Image
    r = mujoco.Renderer(player.m, height=300, width=400)
    cam = mujoco.MjvCamera()
    frames, n = [], cols * rows
    every = max(1, int(seconds / player.env.dt / n))
    ended_at = None
    for i in range(n * every):
        if player.step() and ended_at is None:
            ended_at = i * player.env.dt
        if i % every == every - 1:
            look, azimuth = player.framing()
            if i < every:
                held = azimuth          # one camera position for the whole sheet, chosen from the first frame
            cam.lookat[:] = look
            cam.distance, cam.azimuth, cam.elevation = 3.4, held, -10.0
            r.update_scene(player.d, cam)
            frames.append(r.render().copy())
    grid = np.concatenate([np.concatenate(frames[i * cols:(i + 1) * cols], axis=1) for i in range(rows)], axis=0)
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    Image.fromarray(grid).save(path)
    s = player.env.get_stats()
    print(f"wrote {path}: {seconds:g} s of {player.stage} at iteration {player.iters}, one frame every {every * player.env.dt:.2f} s")
    if player.stage == "getup":
        print(f"  on its feet at the end of {s['up_rate_unaided']:.0%} of the falls, {s['time_to_stand_unaided']:.1f} s after the drives came back")
        return
    print(f"  first episode end at {ended_at if ended_at is not None else 'never'} s | hits/s {s['hits_per_s']:.2f} "
          f"(head {s['head_share']:.0%}) at {s['hit_speed']:.1f} m/s, hardest {s['hit_speed_max']:.1f} | fall rate {s['fall_rate']:.2f} "
          f"| upright {s['upright']:.2f} | distance {s['distance']:.2f} m | glove speed {s['glove_speed']:.1f} m/s")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--ckpt", nargs="*", default=[], help="one checkpoint, or two for a match (first fighter first)")
    ap.add_argument("--run", default="", help="a run name under checkpoints/; the newest checkpoint is used and followed")
    ap.add_argument("--xml", default="")
    ap.add_argument("--sheet", default="", help="write a contact sheet PNG here instead of opening the viewer")
    ap.add_argument("--seconds", type=float, default=8.0)
    ap.add_argument("--speed", type=float, default=1.0)
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--sample", action="store_true", help="act with the exploration noise on, as in training")
    ap.add_argument("--device", default="cpu",
                    help="cpu (default) or cuda. On the CPU it costs a training run on the same machine nothing; "
                         "on the GPU a viewer was measured taking training from 72,000 steps a second to 39,000.")
    args = ap.parse_args()

    ckpt = args.ckpt or newest(args.run)
    player = Player(ckpt, args.xml, args.seed, deterministic=not args.sample, device=args.device)
    if args.sheet:
        sheet(player, args.sheet, args.seconds)
        return

    import mujoco.viewer
    print(f"{player.stage} policy at iteration {player.iters}: {' + '.join(os.path.basename(c) for c in ckpt)}")
    print("drag to orbit, scroll to zoom, space pauses. Close the window to stop.")
    last_check = time.time()
    with mujoco.viewer.launch_passive(player.m, player.d) as v:
        v.cam.distance, v.cam.elevation, v.cam.azimuth = 4.0, -14.0, 110.0
        while v.is_running():
            tick = time.perf_counter()
            ended = player.step()
            v.cam.lookat[:] = player.focus()
            v.sync()
            if ended:
                time.sleep(0.6)
            if args.run and time.time() - last_check > 45.0:
                last_check = time.time()
                latest = newest(args.run)
                if latest != player.ckpt:
                    try:
                        player.load(latest)
                        print(f"now showing iteration {player.iters}", flush=True)
                    except Exception as e:   # the trainer may be half way through writing it
                        print(f"could not load {os.path.basename(latest)} yet: {e}", flush=True)
            lag = player.env.dt / max(args.speed, 1e-3) - (time.perf_counter() - tick)
            if lag > 0:
                time.sleep(lag)


if __name__ == "__main__":
    main()
