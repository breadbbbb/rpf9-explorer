#!/usr/bin/env python3
"""Generates fixtures.json for BcpackBptcCheck from Leonida's tools/bcpack.py.

Every chunk is a synthetic BCPack BC6H/BC7 stream built from random lane symbols (no game data). For each chunk
the reference decoder in tools/bcpack.py is run in strict and provisional mode and its output bytes, provisional
labels or failure kind are recorded; BcpackBptcCheck requires the C# decoder to match them exactly.

    python3 -I make_fixtures.py --leonida <path to a Leonida checkout> [--out fixtures.json]
"""
import argparse
import base64
import importlib.util
import json
import os
import random
import sys


def load_modules(leonida):
    sys.path.insert(0, os.path.join(leonida, "tools"))
    import bcpack
    import bcpack_tables_bptc as bptc
    spec = importlib.util.spec_from_file_location("leonida_test_bcpack", os.path.join(leonida, "tests", "test_bcpack.py"))
    helpers = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(helpers)
    return bcpack, bptc, helpers


class Generator:
    def __init__(self, leonida, seed):
        self.bcpack, self.bptc, self.helpers = load_modules(leonida)
        self.rng = random.Random(seed)
        cells = sorted(((a, b) for a in range(32) for b in range(16)), key=lambda ab: ((ab[0] + 1) * (ab[1] + 1), ab[0]))
        self.cell_of_rank = cells

    # --- lane construction -------------------------------------------------

    def delta_symbols(self, de, di):
        """Lane 0/1 symbol pairs that make the reference rebuilder compute endpoint distance de and index distance di."""
        diff = (de - di) & 255
        signed = diff - 256 if diff >= 128 else diff
        l0 = self.helpers._deint_n(self.helpers._zig(signed), 4)
        l1 = self.helpers._deint_n(di, 4)
        return l0, l1

    def random_block(self, codec, i, sig, de, di):
        """One block as {lane: (symbol_a, symbol_b)} for lanes 0..15."""
        widths = self.bcpack.LANE_WIDTHS[codec]
        block = {k: (self.rng.randrange(1 << widths[k]), self.rng.randrange(1 << widths[k])) for k in range(2, 15)}
        block[0], block[1] = self.delta_symbols(de, di)
        block[15] = sig
        return block

    def lanes_of(self, blocks, codec):
        lanes = [[] for _ in range(16)]
        for block in blocks:
            for k in range(16):
                lanes[k].extend(block[k])
        return lanes

    def encode(self, blocks, codec):
        return self.helpers.encode_lanes(self.lanes_of(blocks, codec), codec)

    def distances(self, i, chained):
        """(de, di) in-chunk references: 0 (none) or 1..min(i, 255)."""
        if not chained or i == 0 or self.rng.random() < 0.3:
            return 0, 0
        top = min(i, 255)
        return self.rng.randint(0, top), self.rng.randint(0, top)

    # --- reference decoding ------------------------------------------------

    def reference(self, packed, codec, size):
        results = []
        for allow in (False, True):
            entry = {"allowProvisional": allow}
            try:
                data, labels = self.bcpack.decode_with_info(packed, codec, size, allow_provisional=allow)
                entry.update(result="ok", data=base64.b64encode(data).decode(), labels=sorted(labels))
            except self.bcpack.BcpackProvisionalError:
                entry["result"] = "provisional"
            except self.bcpack.BcpackError:
                entry["result"] = "invalid"
            results.append(entry)
        return results

    def chunk(self, name, blocks, codec):
        packed = self.encode(blocks, codec)
        size = 16 * len(blocks)
        return {"name": name, "codec": codec, "size": size, "packed": base64.b64encode(packed).decode(),
                "cases": self.reference(packed, codec, size)}

    def strict_ok(self, blocks, codec):
        try:
            self.bcpack.decode(self.encode(blocks, codec), codec, 16 * len(blocks), allow_provisional=False)
            return True
        except self.bcpack.BcpackError:
            return False

    def grow(self, codec, count, make_sig, chained):
        """Random blocks, each kept only if the strict reference decoder accepts the chunk so far."""
        blocks = []
        while len(blocks) < count:
            i = len(blocks)
            de, di = self.distances(i, chained)
            candidate = self.random_block(codec, i, make_sig(i), de, di)
            if self.strict_ok(blocks + [candidate], codec):
                blocks.append(candidate)
        return blocks

    # --- chunk families ----------------------------------------------------

    def bc6h_sig(self, mode=None, part=None):
        return lambda i: (self.rng.randrange(14) if mode is None else mode, self.rng.randrange(32) if part is None else part)

    def bc7_sig_of_rank(self, rank):
        return self.cell_of_rank[rank]

    def bc7_ranks(self):
        config = self.bptc.BC7_CONFIG
        return [r for r in range(len(config)) if config[r][0] <= 7]

    def build(self):
        chunks = []
        for mode in range(14):
            for part in (0, 17 if mode < 10 else 31):
                chunks.append(self.chunk(f"bc6h-mode{mode + 1}-part{part}", self.grow(6, 1, self.bc6h_sig(mode, part), False), 6))
        chunks.append(self.chunk("bc6h-chain-40", self.grow(6, 40, self.bc6h_sig(), True), 6))
        chunks.append(self.chunk("bc6h-chain-300", self.grow(6, 300, self.bc6h_sig(), True), 6))

        inferred = sorted(self.bptc.BC7_CONFIG_INFERRED)
        strict_ranks = [r for r in self.bc7_ranks() if r not in self.bptc.BC7_CONFIG_INFERRED]
        ranks = iter(strict_ranks)
        chunks.append(self.chunk("bc7-every-signature", self.grow(7, len(strict_ranks), lambda i: self.bc7_sig_of_rank(strict_ranks[i]), True), 7))
        chunks.append(self.chunk("bc7-chain-300", self.grow(7, 300, lambda i: self.bc7_sig_of_rank(self.rng.choice(strict_ranks)), True), 7))
        chunks.append(self.chunk("bc7-inferred-signatures", self.inferred_blocks(inferred), 7))

        for codec, sigs in ((6, lambda i: (self.rng.randrange(14), self.rng.randrange(32))),
                            (7, lambda i: self.bc7_sig_of_rank(self.rng.choice(self.bc7_ranks())))):
            for n in range(24):
                count = 1 + n % 6
                blocks = [self.random_block(codec, i, sigs(i), *self.distances(i, n % 2 == 1)) for i in range(count)]
                chunks.append(self.chunk(f"bc{codec}-random-{n}", blocks, codec))
            for n in range(8):
                blocks = self.grow(codec, 3, sigs, True)
                blocks.insert(self.rng.randrange(3), self.random_block(codec, 0, sigs(0), self.rng.randint(1, 255), self.rng.randint(0, 255)))
                chunks.append(self.chunk(f"bc{codec}-history-reference-{n}", blocks, codec))
        chunks.extend(self.per_mode_chunks())
        return chunks

    def per_mode_chunks(self):
        """Unfiltered single blocks of every mode: the failure branches (anchor, p-bit, inferred signature) as well as clean decodes."""
        chunks = []
        config = self.bptc.BC7_CONFIG
        for mode in range(8):
            ranks = [r for r in self.bc7_ranks() if config[r][0] == mode]
            for n in range(6):
                block = self.random_block(7, 0, self.bc7_sig_of_rank(self.rng.choice(ranks)), 0, 0)
                chunks.append(self.chunk(f"bc7-mode{mode}-single-{n}", [block], 7))
        for n in range(3):
            chunks.append(self.chunk(f"bc7-pbit-inconsistent-{n}", [self.block_with_status(range(8), 16, [1])], 7))
        for mode in range(8):
            chunks.append(self.chunk(f"bc7-anchor-inconsistent-mode{mode}", [self.block_with_status(range(8), 8, [mode])], 7))
        inferred = sorted(self.bptc.BC7_CONFIG_INFERRED)
        for kind, status, modes in (("pbit", 16, [1]), ("anchor", 8, [6])):
            blocks = self.inferred_blocks(inferred[:1]) + [self.block_with_status(range(8), status, modes)]
            chunks.append(self.chunk(f"bc7-{kind}-inconsistent-with-label", blocks, 7))
        for rank in sorted(self.bptc.BC7_CONFIG_INFERRED)[:4]:
            blocks = [self.random_block(7, i, self.bc7_sig_of_rank(rank), 0, 0) for i in range(3)]
            chunks.append(self.chunk(f"bc7-inferred-unfiltered-{rank}", blocks, 7))
        for mode in range(14):
            for n in range(3):
                block = self.random_block(6, 0, (mode, self.rng.randrange(32)), 0, 0)
                chunks.append(self.chunk(f"bc6h-mode{mode + 1}-single-{n}", [block], 6))
        return chunks

    def block_with_status(self, _modes, want, modes):
        """A random single BC7 block of one of `modes` whose reference rebuild reports exactly the status flag `want`."""
        config = self.bptc.BC7_CONFIG
        ranks = [r for r in self.bc7_ranks() if config[r][0] in modes and r not in self.bptc.BC7_CONFIG_INFERRED]
        table = self.bptc.load_bc7()
        while True:
            block = self.random_block(7, 0, self.bc7_sig_of_rank(self.rng.choice(ranks)), 0, 0)
            _, status = self.bcpack._bc7_rebuild(self.lanes_of([block], 7), 1, table)
            if status[0] == want:
                return block

    def inferred_blocks(self, inferred):
        blocks = []
        for rank in inferred:
            while True:
                candidate = self.random_block(7, len(blocks), self.bc7_sig_of_rank(rank), 0, 0)
                try:
                    self.bcpack.decode(self.encode(blocks + [candidate], 7), 7, 16 * (len(blocks) + 1), allow_provisional=True)
                    blocks.append(candidate)
                    break
                except self.bcpack.BcpackError:
                    pass
        return blocks

    def pool_chunks(self):
        """Two 256-block BC7 chunks for the synthetic texture pool: one strict-clean, one that reads built-in history once."""
        config = self.bptc.BC7_CONFIG
        ranks = [r for r in self.bc7_ranks() if r not in self.bptc.BC7_CONFIG_INFERRED and config[r][0] in (6, 5, 4, 1)]
        clean = self.grow(7, 256, lambda i: self.bc7_sig_of_rank(self.rng.choice(ranks)), True)
        referencing = list(clean)
        while True:
            # Distance above the block's position (40): reads the built-in history, not an earlier block.
            candidate = self.random_block(7, 40, self.bc7_sig_of_rank(self.rng.choice(ranks)), self.rng.randint(41, 255), 0)
            trial = referencing[:40] + [candidate] + referencing[41:]
            try:
                self.bcpack.decode(self.encode(trial, 7), 7, 4096, allow_provisional=True)
            except self.bcpack.BcpackError:
                continue
            return [self.chunk("pool-clean", clean, 7), self.chunk("pool-history-reference", trial, 7)]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--leonida", required=True)
    ap.add_argument("--out", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "fixtures.json"))
    ap.add_argument("--seed", type=int, default=315)
    args = ap.parse_args()
    gen = Generator(args.leonida, args.seed)
    document = {"chunks": gen.build(), "pool": gen.pool_chunks()}
    with open(args.out, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(document, fh, separators=(",", ":"))
        fh.write("\n")
    counts = {}
    for chunk in document["chunks"]:
        for case in chunk["cases"]:
            key = (chunk["codec"], case["allowProvisional"], case["result"])
            counts[key] = counts.get(key, 0) + 1
    for key in sorted(counts):
        print(key, counts[key])


if __name__ == "__main__":
    main()
