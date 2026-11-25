// Unit tests for the parser embedded in original GeoZamir UI, adapted to RoundUp.
const fs = require("node:fs");
const vm = require("node:vm");
const assert = require("node:assert/strict");
const src = fs.readFileSync("src/RoundUp.UI/app.js", "utf8");
function between(a, b) {
  const start = src.indexOf(a), end = src.indexOf(b, start + a.length);
  if (start < 0 || end < 0) throw Error("Missing parser entry: " + a);
  return src.slice(start, end);
}
const context = {
  Ze: x => Number(x),
  j: p => Number.isFinite(p?.lat) && Number.isFinite(p?.lng) &&
    Math.abs(p.lat) <= 90 && Math.abs(p.lng) <= 180,
  be: {},
  qe: /\.(jpe?g|png|webp)$/i,
  T: p => String(p).split(/[\\/]/).pop(),
  x: true
};
vm.createContext(context);
vm.runInContext(
  between("function Ot(", "function tt(") +
  between("function it(", "function ct(") +
  between("function ct(", "async function Ht(") +
  between("async function Ht(", "function lt(") +
  "this.parser={Ot,it,ct,Ht};",
  context, {timeout:1500}
);
const { Ot, it, ct, Ht } = context.parser;
const near = (got, expected) => assert.ok(Math.abs(got - expected) < 0.00001,
  "Expected " + expected + ", got " + got);
for (const input of [
  {geo:{lat:49.833,lng:24.031}},
  {location:{latitude:"49.833",longitude:"24.031"}},
  {geometry:{type:"Point",coordinates:[24.031,49.833]}},
  {coordinates:[24.031,49.833]},
  {coordinates:[49.833,24.031]},
  {lat:"49.833",lon:"24.031"},
  {position:{coords:{latitude:49.833,longitude:24.031}}}
]) {
  const loc = Ot(input);
  assert.ok(loc,"Could not locate " + JSON.stringify(input));
  near(loc.lat,49.833);
  near(loc.lng,24.031);
}
const report = {
  meta:{manager:"Марія"},
  entries:[{
    id:"point-1",
    location:{latitude:49.833,longitude:24.031},
    comment:"Проїхали вулицю; будівництва немає",
    photos:[{path:"photos/001.jpg",comment:"Фасад, 25.11.2026"}]
  }]
};
const data = it(report);
assert.equal(data.length,1);
assert.equal(data[0].geo.latitude,49.833);
const item = ct(data[0],"report.zip","entries.json");
near(item.geo.lat,49.833);
assert.equal(item.meta.comment,"Проїхали вулицю; будівництва немає");
assert.equal(item.meta.manager,"Марія");
assert.equal(item.rawPhotos.length,1);
assert.equal(item.rawPhotos[0].comment,"Фасад, 25.11.2026");
assert.equal(it([{lat:49.8,lng:24.0,comment:"Без фото"}]).length,1);
async function testPhoto() {
  const img = { name:"photos/001.jpg", dir:false, date:new Date("2026-11-25"), async:async()=>new Uint8Array([1,2]) };
  const zip = {files:{a:img}};
  const r = await Ht(zip,{measurements:[],rawPhotos:[{path:"photos/001.jpg",comment:"Видно новий фасад"}],warnings:[]},"data.json",true);
  assert.equal(r.length,1);
  assert.equal(r[0].note,"Видно новий фасад");
  console.log("PASS RoundUp: 7 GPS formats, object comments and ZIP photo note transfer.");
}
testPhoto().catch(e=>{console.error(e);process.exit(1)});
