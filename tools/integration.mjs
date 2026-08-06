// Integration flow for the registration + inspection endpoints.
// Needs a running Superserver (default http://localhost:8002) with the
// Hessigheim dataset. Run: node tools/integration.mjs [baseUrl]
//
// Flow: seed correspondence points via /query/closest, perturb one mesh by a
// known rigid T, build pairs, /query/lsq-pairs must recover ≈ T⁻¹; run
// /query/pair-error with the pre- and post-correction poses and assert the
// pair median error shrinks (plus swap symmetry, determinism, the per-pin
// overlap gate). Then exercise /query/region-distance (per-vertex array, the
// no-Z-overlap sentinel), /query/pair-error-at (exact picked point,
// antisymmetry, lift tracking) and /query/pair-overlap.

const base = (process.argv[2] || "http://localhost:8002") + "/api";

let failures = 0, total = 0;
function check(name, cond, detail) {
  total++;
  if (cond) console.log(`ok    ${name}${detail ? " (" + detail + ")" : ""}`);
  else { failures++; console.log(`FAIL  ${name}${detail ? " (" + detail + ")" : ""}`); }
}

async function post(path, body) {
  const r = await fetch(base + path, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(body),
  });
  return r;
}
async function postJson(path, body) {
  const r = await post(path, body);
  if (!r.ok) throw new Error(`${path} → HTTP ${r.status}: ${await r.text()}`);
  return await r.json();
}

// minimal rigid/matrix helpers (row-major 4×4, column-vector convention)
const mul = (a, b) => {
  const o = new Array(16).fill(0);
  for (let i = 0; i < 4; i++)
    for (let j = 0; j < 4; j++)
      for (let k = 0; k < 4; k++) o[i * 4 + j] += a[i * 4 + k] * b[k * 4 + j];
  return o;
};
const apply = (m, p) => [
  m[0] * p[0] + m[1] * p[1] + m[2] * p[2] + m[3],
  m[4] * p[0] + m[5] * p[1] + m[6] * p[2] + m[7],
  m[8] * p[0] + m[9] * p[1] + m[10] * p[2] + m[11],
];
const sub = (a, b) => a.map((v, i) => v - b[i]);
const len = (a) => Math.hypot(...a);

function rigidAbout(axisAngle, centre, t) {
  // rotation about z by angle through `centre`, plus translation t
  const c = Math.cos(axisAngle), s = Math.sin(axisAngle);
  const R = [c, -s, 0, s, c, 0, 0, 0, 1];
  const rc = [
    R[0] * centre[0] + R[1] * centre[1] + R[2] * centre[2],
    R[3] * centre[0] + R[4] * centre[1] + R[5] * centre[2],
    R[6] * centre[0] + R[7] * centre[1] + R[8] * centre[2],
  ];
  const tr = [centre[0] - rc[0] + t[0], centre[1] - rc[1] + t[1], centre[2] - rc[2] + t[2]];
  return [R[0], R[1], R[2], tr[0], R[3], R[4], R[5], tr[1], R[6], R[7], R[8], tr[2], 0, 0, 0, 1];
}
const invertRigid = (m) => {
  // transpose rotation, t' = -Rᵀ t
  const R = [m[0], m[4], m[8], m[1], m[5], m[9], m[2], m[6], m[10]];
  const t = [m[3], m[7], m[11]];
  const ti = [
    -(R[0] * t[0] + R[1] * t[1] + R[2] * t[2]),
    -(R[3] * t[0] + R[4] * t[1] + R[5] * t[2]),
    -(R[6] * t[0] + R[7] * t[1] + R[8] * t[2]),
  ];
  return [R[0], R[1], R[2], ti[0], R[3], R[4], R[5], ti[1], R[6], R[7], R[8], ti[2], 0, 0, 0, 1];
};

const run = async () => {
  const datasets = await (await fetch(base + "/datasets")).json();
  check("datasets available", datasets.includes("Hessigheim"), datasets.join(","));
  const centroids = await (await fetch(base + "/datasets/Hessigheim/centroids")).json();
  const names = Object.keys(centroids).sort();
  check("≥2 meshes in Hessigheim", names.length >= 2, names.join(","));
  const refMesh = "Hessigheim/" + names[0];
  const movMesh = "Hessigheim/" + names[1];
  const movCentroid = centroids[names[1]];

  // 1 · seed surface points on the moving mesh via /query/closest
  const offsets = [
    [25, 0, 0], [-25, 10, 0], [0, 30, 0], [12, -22, 0], [-18, -15, 0], [30, 25, 0],
  ];
  const seeds = offsets.map((o) => movCentroid.map((v, i) => v + o[i]));
  const surfPts = [];
  for (const s of seeds) {
    const r = await postJson("/query/closest", { name: movMesh, index: 0, point: s });
    if (r.found) surfPts.push(r.point);
  }
  check("seeded ≥4 surface points", surfPts.length >= 4, `${surfPts.length}`);

  // 2 · perturb by a known rigid T (rotation about the centroid + offset)
  const T = rigidAbout(0.01, movCentroid, [0.3, -0.2, 0.6]);
  const Tinv = invertRigid(T);

  // 3 · lsq-pairs over (p, T(p)) must recover T⁻¹ to numerical precision
  const pairs = surfPts.map((p) => ({ refPoint: p, movingPoint: apply(T, p), weight: 1.0 }));
  const lsq = await postJson("/query/lsq-pairs", { movingName: movMesh, pairs });
  // Compare the transforms by their action on the test points — raw matrix
  // entries amplify any last-ulp rotation difference by the ~5e5 m UTM lever.
  const actionErr = Math.max(...surfPts.map((p) => {
    const m = apply(T, p);
    return len(sub(apply(lsq.transform, m), apply(Tinv, m)));
  }));
  check("lsq-pairs recovers T⁻¹", actionErr < 1e-6, `maxΔ=${actionErr.toExponential(2)} m`);
  check("lsq-pairs residuals ≈ 0", Math.max(...lsq.perPairResiduals) < 1e-6,
        `max=${Math.max(...lsq.perPairResiduals).toExponential(2)}`);
  check("lsq-pairs not collinear", lsq.conditioning.collinearityWarning === false);
  check("conditioning eigenvalues descending",
        lsq.conditioning.eigenvalues[0] >= lsq.conditioning.eigenvalues[1]
        && lsq.conditioning.eigenvalues[1] >= lsq.conditioning.eigenvalues[2]);

  // <3 pairs → HTTP 400
  const bad = await post("/query/lsq-pairs", { movingName: movMesh, pairs: pairs.slice(0, 2) });
  check("lsq-pairs <3 pairs → 400", bad.status === 400, `${bad.status}`);

  // collinear pairs → warning
  const linePairs = [0, 1, 2, 3].map((i) => {
    const p = [movCentroid[0] + i * 10, movCentroid[1], movCentroid[2]];
    return { refPoint: p, movingPoint: apply(T, p), weight: 1.0 };
  });
  const lin = await postJson("/query/lsq-pairs", { movingName: movMesh, pairs: linePairs });
  check("collinear pairs flag warning", lin.conditioning.collinearityWarning === true);

  // 4 · pair-error: reference-free symmetric error at explicit poses. Real
  // inter-epoch change between the two meshes confounds an absolute-median
  // comparison, so assert relative to the unperturbed baseline: the
  // perturbation must move the pair median away from it, and the exact lsq
  // correction must restore it — i.e. the perturbation-induced error shrinks.
  const refCentroid = centroids[names[0]];
  const refSeed = await postJson("/query/closest", {
    name: refMesh, index: 0, point: [refCentroid[0], refCentroid[1], refCentroid[2]],
  });
  check("pin centre seeded on the surface", refSeed.found === true);
  const probeAt = refSeed.point;
  const identity = [1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1];
  const pairError = async (tA, tB, pins) =>
    await postJson("/query/pair-error", {
      meshA: { name: refMesh, transform: tA },
      meshB: { name: movMesh, transform: tB },
      pins: pins || [{ id: "p0", centre: probeAt, radius: 20 }],
      length: 0, maxPointsPerMesh: 4096,
    });
  const baseline = await pairError(identity, identity);
  const p0 = baseline.pins[0];
  check("pair-error returns the pin", baseline.pins.length === 1 && p0.id === "p0" && p0.ok === true);
  check("pair-error samples aligned with positions",
        p0.count > 0 && p0.samples.length > 0 && p0.positions.length === 3 * p0.samples.length,
        `${p0.count} pooled samples`);
  check("pair-error lodHalfWidth > 0", p0.lodHalfWidth > 0, `±${(p0.lodHalfWidth * 1000).toFixed(1)} mm`);
  const again = await pairError(identity, identity);
  check("pair-error deterministic for fixed inputs",
        again.pins[0].median === p0.median && again.pins[0].count === p0.count);
  // symmetric measure, antisymmetric sign: swapping A and B negates the median
  const swapped = await postJson("/query/pair-error", {
    meshA: { name: movMesh, transform: identity },
    meshB: { name: refMesh, transform: identity },
    pins: [{ id: "p0", centre: probeAt, radius: 20 }],
    length: 0, maxPointsPerMesh: 4096,
  });
  check("pair-error swap negates the median",
        swapped.pins[0].ok === true && Math.abs(swapped.pins[0].median + p0.median) < 5e-3,
        `${p0.median.toFixed(4)} vs ${swapped.pins[0].median.toFixed(4)} m`);
  check("pair-error swap keeps the LoD",
        Math.abs(swapped.pins[0].lodHalfWidth - p0.lodHalfWidth) < 5e-3);
  const pre = await pairError(identity, T);
  const post_ = await pairError(identity, mul(lsq.transform, T));   // exact correction from §3
  check("pair-error ok baseline/pre/post",
        p0.ok === true && pre.pins[0].ok === true && post_.pins[0].ok === true);
  const errPre = Math.abs(pre.pins[0].median - p0.median);
  const errPost = Math.abs(post_.pins[0].median - p0.median);
  check("perturbation shifts the pair median", errPre > 0.2,
        `baseline ${p0.median.toFixed(3)} → perturbed ${pre.pins[0].median.toFixed(3)} m`);
  check("correction shrinks the median error", errPost < errPre && errPost < 0.05,
        `${errPre.toFixed(3)} → ${errPost.toFixed(3)} m`);
  // per-pin overlap gate: a far-away pin fails alone inside the same batch
  const mixed = await pairError(identity, identity, [
    { id: "good", centre: probeAt, radius: 20 },
    { id: "far", centre: [probeAt[0] + 1e5, probeAt[1], probeAt[2]], radius: 20 },
  ]);
  check("pair-error per-pin overlap gate",
        mixed.pins.length === 2
        && mixed.pins.find((p) => p.id === "good").ok === true
        && mixed.pins.find((p) => p.id === "far").ok === false);

  // 5 · region-distance: per-vertex array, sentinel outside Z-overlap.
  const rdReq = (targetTransform) => ({
    targetName: movMesh, targetIndex: 0, refName: refMesh, refIndex: 0,
    targetTransform, refTransform: identity,
  });
  const rd0 = await postJson("/query/region-distance", rdReq(identity));
  const responders = rd0.dist.filter((v) => Math.abs(v) < 1e20);
  check("region-distance per-vertex array with responders",
        Array.isArray(rd0.dist) && rd0.dist.length > 100 && responders.length > 0,
        `${responders.length}/${rd0.dist.length} respond`);
  // A target moved 100 km away shares no vertical support → all sentinels.
  const far = [1,0,0,1e5, 0,1,0,0, 0,0,1,0, 0,0,0,1];
  const rdFar = await postJson("/query/region-distance", rdReq(far));
  check("no Z-overlap ⇒ sentinel everywhere", rdFar.dist.every((v) => Math.abs(v) >= 1e20));

  // 6 · pair-error-at: exact value at a picked point.
  const lift = [1,0,0,0, 0,1,0,0, 0,0,1,5, 0,0,0,1];
  const atReq = (a, ta, b, tb, pt) => ({
    meshA: { name: a, transform: ta }, meshB: { name: b, transform: tb },
    point: pt, radius: 5, maxDist: 50,
  });
  const at = await postJson("/query/pair-error-at", atReq(refMesh, identity, movMesh, identity, probeAt));
  check("pair-error-at ok with finite value", at.ok === true && Number.isFinite(at.value),
        at.ok ? `${at.value.toFixed(4)} m` : at.reason);
  const atSwap = await postJson("/query/pair-error-at", atReq(movMesh, identity, refMesh, identity, probeAt));
  check("pair-error-at antisymmetric on swap",
        atSwap.ok === true && Math.abs(atSwap.value + at.value) < 1e-4,
        `${at.value.toFixed(6)} vs ${atSwap.value.toFixed(6)} m`);
  // lifting B +5 m raises the value by ≈ 5 m (terrain-slope tolerance)
  const atUp = await postJson("/query/pair-error-at", atReq(refMesh, identity, movMesh, lift, probeAt));
  check("pair-error-at tracks a +5 m lift",
        atUp.ok === true && atUp.value - at.value > 4 && atUp.value - at.value < 6,
        `Δ=${(atUp.value - at.value).toFixed(3)} m`);
  const atFar = await postJson("/query/pair-error-at",
    atReq(refMesh, identity, movMesh, identity, [probeAt[0] + 1e5, probeAt[1], probeAt[2]]));
  check("pair-error-at off-surface → not ok", atFar.ok === false);

  // 7 · pair-overlap: registerability at supplied poses.
  const ovReq = (tB) => ({
    meshA: { name: refMesh, transform: identity },
    meshB: { name: movMesh, transform: tB },
    maxDist: 0, minFraction: 0, maxSamples: 0,
  });
  const ov = await postJson("/query/pair-overlap", ovReq(identity));
  check("pair-overlap: co-located epochs sufficient", ov.sufficient === true,
        `A→B ${ov.fracAB.toFixed(2)}, B→A ${ov.fracBA.toFixed(2)}, d≤${ov.maxDist.toFixed(1)} m`);
  check("pair-overlap fractions sane",
        ov.fracAB > 0.2 && ov.fracAB <= 1 && ov.fracBA > 0.2 && ov.fracBA <= 1);
  const ovFar = await postJson("/query/pair-overlap", ovReq(far));
  check("pair-overlap: disjoint → insufficient",
        ovFar.sufficient === false && ovFar.fracAB === 0 && ovFar.fracBA === 0);

  // 8 · roi-fit: adaptive ROI radius against the other pair mesh.
  const rfReq = (centre, radius, tOther) => ({
    otherName: movMesh, otherTransform: tOther ?? identity,
    centre, radius, minVerts: 20, maxFactor: 4,
  });
  const rf = await postJson("/query/roi-fit", rfReq(probeAt, 20));
  check("roi-fit: co-located surfaces fit at the default radius",
        rf.ok === true && rf.radius >= 20 && rf.radius <= 80,
        `r=${rf.radius.toFixed(2)} (${rf.count} verts)`);
  const rfTiny = await postJson("/query/roi-fit", rfReq(probeAt, 0.001));
  check("roi-fit: a tiny radius grows toward the other mesh",
        rfTiny.ok === false || rfTiny.radius > 0.001,
        rfTiny.ok ? `grew to ${rfTiny.radius.toFixed(4)}` : "refused past cap");
  const rfFar = await postJson("/query/roi-fit",
    rfReq([probeAt[0] + 1e5, probeAt[1], probeAt[2]], 20));
  check("roi-fit: unreachable other mesh refused", rfFar.ok === false && rfFar.count < 20);

  console.log("");
  console.log(`${total - failures}/${total} passed${failures ? ` — ${failures} FAILED` : ""}`);
  process.exit(failures);
};

run().catch((e) => { console.error("integration aborted:", e.message); process.exit(99); });
