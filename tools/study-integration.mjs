// Study-mode integration (spec §12.2/§12.3) against a running server:
//   ASPNETCORE_URLS=http://localhost:8002 dotnet run --project src/Superserver
//   node tools/study-integration.mjs
// Walks the entire glacier-v1 config as a real tokened session, checks
// advance ordering, the tutorial screen-out path, completion gating, resume,
// NUM feature filtering and route security.

const base = process.env.SUPER_URL ?? 'http://localhost:8002';
let passed = 0, failed = 0;
const check = (name, cond, extra = '') => {
  if (cond) { passed++; console.log(`ok    ${name}`); }
  else { failed++; console.log(`FAIL  ${name} ${extra}`); }
};

const post = async (path, body) => {
  const r = await fetch(base + path, {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  let json = null;
  try { json = await r.json(); } catch { /* 204 */ }
  return { status: r.status, json };
};
const get = async (path) => {
  const r = await fetch(base + path);
  let json = null;
  try { json = await r.json(); } catch { }
  return { status: r.status, json };
};

// answers keyed by question id (match secret.json)
const answers = {
  T1: 0, T2: 0, T3: 0, QA1: 2,
  QA2: [-176000.0, -2279000.0, 300.0],
  QC1: 0, QC2: 0, QC3: 0,
  QD1: 'I kept the landmark-based result because bedrock is stable.',
  F1: -12.0, F2: -8.0,
  'QE-final': 'The glacier tongue thinned considerably between the epochs.',
  D1: 1, D2: 1,
};
const gridAnswer = (n, v) => Object.fromEntries(Array.from({ length: n }, (_, i) => [i, v]));

const main = async () => {
  // tokens (localhost-only admin endpoint)
  const tok = await post('/api/study/glacier-v1/tokens', { n: 8 });
  check('token generation', tok.status === 200 && tok.json.length === 8);
  const tC = tok.json[7];

  // Balanced assignment converges on both conditions within a few creations
  // even on a dirty store (the strict ≤1 property is unit-tested on a clean
  // one — the store here accumulates sessions across runs).
  let full = null, num = null, fullTok = null;
  for (let i = 0; i < 7 && (!full || !num); i++) {
    const s = await post('/api/study/session', { token: tok.json[i] });
    if (s.status !== 200) { check('session creation', false, `status ${s.status}`); break; }
    if (s.json.condition === 'FULL' && !full) { full = s.json; fullTok = tok.json[i]; }
    if (s.json.condition === 'NUM' && !num) num = s.json;
  }
  check('balancing yields both conditions', !!full && !!num);
  const cfg = full.configPublic;

  // configPublic carries no planted answers (key scan, §12.2)
  const cfgText = JSON.stringify(cfg).toLowerCase();
  check('configPublic clean of secret keys',
    !['"secret', '"answers"', '"checkpoints"', 'goldanswer'].some((k) => cfgText.includes(k)));

  // NUM resolved feature set excludes every starred feature (§12.3)
  const starred = ['violinChart', 'heatmap', 'heatmapDiff', 'threeSourceBar', 'splitViolinPreview'];
  const numDisabled = num.configPublic.conditions.NUM.disabledFeatures;
  check('NUM disables all starred features', starred.every((f) => numDisabled.includes(f)));
  const p1 = num.configPublic.phases.find((p) => p.id === 'p1-inspect');
  const resolved = p1.allowedFeatures.filter((f) => !numDisabled.includes(f));
  check('NUM resolved set keeps unstarred features',
    resolved.includes('hoverProbe') && resolved.includes('meshPanel')
    && !resolved.includes('violinChart') && !resolved.includes('heatmap'));

  // route security (§12.2): secret + scores unreachable
  const sid = full.sessionId;
  for (const path of [
    '/studies/glacier-v1/secret.json',
    '/api/studies/glacier-v1/secret.json',
    `/api/study/${sid}/scores`,
    `/api/study/${sid}/secret`,
  ]) {
    const r = await fetch(base + path);
    const text = await r.text();
    check(`unreachable: ${path}`,
      !(r.status === 200 && (text.includes('checkPoints') || text.includes('"tre"'))));
  }

  // out-of-order advance rejected before anything happened
  const ooo = await post(`/api/study/${sid}/advance`, { phaseId: 'p1-inspect', stepId: 'p1-qa1' });
  check('out-of-order advance rejected', ooo.status === 409);

  // events batch lands
  const ev = await post(`/api/study/${sid}/events`, {
    events: [{ t: 1, type: 'sessionStart', payload: {} }, { t: 5, type: 'orbit', payload: {} }],
  });
  check('events batch accepted', ev.status === 204);

  // tutorial gold echo: wrong then right
  const wrong = await post(`/api/study/${sid}/answers`, { questionId: 'T1', value: 1 });
  check('tutorial gold wrong echoed', wrong.status === 200 && wrong.json.correct === false && !wrong.json.screened);
  const right = await post(`/api/study/${sid}/answers`, { questionId: 'T1', value: 0 });
  check('tutorial gold correct echoed', right.status === 200 && right.json.correct === true);
  // non-tutorial gold answers never echo correctness
  const main1 = await post(`/api/study/${sid}/answers`, { questionId: 'QC2', value: 0 });
  check('main-phase gold not echoed', main1.status === 200 && !('correct' in (main1.json ?? {})));

  // walk the entire config in order
  let advanced = 0;
  let resumeChecked = false;
  for (const phase of cfg.phases) {
    for (const step of phase.steps) {
      const q = step.question;
      if (q && answers[q.id] !== undefined) {
        const a = await post(`/api/study/${sid}/answers`, {
          questionId: q.id, value: answers[q.id],
          ...(q.confidence ? { confidence: 5 } : {}),
        });
        if (a.status !== 200) check(`answer ${q.id}`, false, `status ${a.status}`);
      } else if (step.kind === 'questionnaire') {
        const items = cfg.questionnaires[step.questionnaire].length;
        const v = step.questionnaire === 'tlx' ? 40 : 3;
        const a = await post(`/api/study/${sid}/answers`, {
          questionId: step.questionnaire, value: gridAnswer(items, v),
        });
        if (a.status !== 200) check(`questionnaire ${step.questionnaire}`, false, `status ${a.status}`);
      }
      const adv = await post(`/api/study/${sid}/advance`, { phaseId: phase.id, stepId: step.id });
      if (adv.status !== 204) check(`advance ${phase.id}/${step.id}`, false, `status ${adv.status}`);
      advanced++;
      if (phase.id === 'p2-register' && !resumeChecked) {
        resumeChecked = true;
        // §10 resume: same token → same session at the recorded position
        const res = await post('/api/study/session', { token: fullTok });
        check('resume returns same session',
          res.status === 200 && res.json.sessionId === sid && res.json.resumed === true);
        check('resume position recorded',
          res.json.lastPhaseId === phase.id && res.json.lastStepId === step.id);
        // transforms posted mid-study get scored silently (204, nothing back)
        const tr = await post(`/api/study/${sid}/transforms`, {
          label: 'commit#1',
          perMesh: { 'SETSM_glacier/20241005_SETSM_s2s041_WV02_20241005_1030010106420700_1030010106C1AB00_2m_seg1': [1, 0, 0, 5, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1] },
        });
        check('transforms post accepted, no scores returned', tr.status === 204 && tr.json === null);
        // complete must refuse mid-study
        const c = await get(`/api/study/${sid}/complete`);
        check('complete refused mid-study', c.status === 409);
      }
    }
  }
  check('walked all steps', advanced === cfg.phases.reduce((n, p) => n + p.steps.length, 0));

  // completion: refused without a final transforms post, granted after
  const cNoFinal = await get(`/api/study/${sid}/complete`);
  check('complete refused without final transforms', cNoFinal.status === 409);
  const trFinal = await post(`/api/study/${sid}/transforms`, {
    label: 'final',
    perMesh: { 'SETSM_glacier/20241005_SETSM_s2s041_WV02_20241005_1030010106420700_1030010106C1AB00_2m_seg1': [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1] },
  });
  check('final transforms accepted', trFinal.status === 204);
  const ws = await post(`/api/study/${sid}/workspace`, { workspaceJson: '{"version":2}' });
  check('workspace upload accepted', ws.status === 204);
  const done = await get(`/api/study/${sid}/complete`);
  check('completion code issued', done.status === 200 && /^[0-9A-F]{8}$/.test(done.json.code), JSON.stringify(done.json));

  // completed token refuses re-entry
  const again = await post('/api/study/session', { token: fullTok });
  check('completed token refused', again.status === 409);

  // gold screen-out path on a fresh session (§12.3)
  const sC = await post('/api/study/session', { token: tC });
  const sidC = sC.json.sessionId;
  let lastC = null;
  for (let i = 0; i < 3; i++) {
    lastC = await post(`/api/study/${sidC}/answers`, { questionId: 'T1', value: 1 });
  }
  check('third gold fail screens out', lastC.json.screened === true);
  const resC = await post('/api/study/session', { token: tC });
  check('screened token refused', resC.status === 409 && resC.json.error === 'screened');

  console.log(`\n${passed}/${passed + failed} passed${failed ? ` — ${failed} FAILED` : ''}`);
  process.exit(failed ? 1 : 0);
};

main().catch((e) => { console.error(e); process.exit(1); });
