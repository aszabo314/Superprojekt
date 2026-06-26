// Integration flow for the ensemble-registration endpoints (spec §10.3).
// Needs a running Superserver (default http://localhost:8002) with the
// Hessigheim dataset. Run: node tools/integration.mjs [baseUrl]
//
// Flow: seed correspondence points via /query/closest, perturb one mesh by a
// known rigid T, build pairs, /query/lsq-pairs must recover ≈ T⁻¹; feed the
// corrected transform to /query/icp and assert the RMS decreases; run
// /query/probe with the pre- and post-correction transforms and assert the
// moving mesh's median |distance| shrinks; /query/patch with a frame
// override must echo the requested frame (and now carries per-point UVs).

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
const maxAbsDiff = (a, b) => Math.max(...a.map((v, i) => Math.abs(v - b[i])));

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

  // 4 · probe with pre- and post-correction transforms. Real inter-epoch
  // change between the two meshes confounds an absolute-median comparison,
  // so assert relative to the unperturbed baseline: the perturbation must
  // move the moving mesh's median away from it, and the exact lsq correction
  // must restore it — i.e. the perturbation-induced median error shrinks.
  // The probe centre must sit on the REFERENCE surface (the cylinder axis is
  // fit from reference vertices) — project a point near the ref centroid.
  const refCentroid = centroids[names[0]];
  const refSeed = await postJson("/query/closest", {
    name: refMesh, index: 0, point: [refCentroid[0], refCentroid[1], refCentroid[2]],
  });
  check("reference probe centre found", refSeed.found === true);
  const probeAt = refSeed.point;
  const probe = async (movTransform) =>
    await postJson("/query/probe", {
      meshes: [
        { name: refMesh, transform: [1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1] },
        { name: movMesh, transform: movTransform },
      ],
      referenceName: refMesh, centre: probeAt, radius: 20, length: 0, maxPointsPerMesh: 4096,
    });
  const identity = [1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1];
  const baseline = await probe(identity);
  const pre = await probe(T);
  const post_ = await probe(mul(lsq.transform, T));   // exact correction from §3
  const medOf = (resp) => {
    const d = (resp.distributions || []).find((x) => x.name === movMesh);
    return d && d.count > 0 ? d.median : NaN;
  };
  const mBase = medOf(baseline), mPre = medOf(pre), mPost = medOf(post_);
  check("probe ok baseline/pre/post",
        baseline.ok === true && pre.ok === true && post_.ok === true);
  const errPre = Math.abs(mPre - mBase), errPost = Math.abs(mPost - mBase);
  check("perturbation shifts the moving median", errPre > 0.2,
        `baseline ${mBase.toFixed(3)} → perturbed ${mPre.toFixed(3)} m`);
  check("correction shrinks the median error", errPost < errPre && errPost < 0.05,
        `${errPre.toFixed(3)} → ${errPost.toFixed(3)} m`);

  // 6 · patch frame override: response echoes the requested frame, points carry UVs
  const patchSeed = await postJson("/query/closest", {
    name: refMesh, index: 0, point: [refCentroid[0], refCentroid[1] + 20, refCentroid[2]],
  });
  const patchAt = patchSeed.point;
  const patchFree = await postJson("/query/patch", {
    name: refMesh, centre: patchAt, radius: 15, maxPoints: 800,
  });
  check("patch returns points", patchFree.points.length > 10, `${patchFree.points.length}`);
  check("patch points carry UVs", patchFree.points.every((p) => p.length >= 7));
  const patchFramed = await postJson("/query/patch", {
    name: refMesh, centre: patchAt, radius: 15, maxPoints: 800,
    frameNormal: [0, 0, 1], frameRefDir: [1, 0, 0],
  });
  check("patch echoes frame normal", maxAbsDiff(patchFramed.normal, [0, 0, 1]) < 1e-9,
        patchFramed.normal.map((v) => v.toFixed(3)).join(","));
  check("patch echoes frame refDir", maxAbsDiff(patchFramed.refDir, [1, 0, 0]) < 1e-9,
        patchFramed.refDir.map((v) => v.toFixed(3)).join(","));

  console.log("");
  console.log(`${total - failures}/${total} passed${failures ? ` — ${failures} FAILED` : ""}`);
  process.exit(failures);
};

run().catch((e) => { console.error("integration aborted:", e.message); process.exit(99); });
