using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using FontShelf.Models;

namespace FontShelf.Services
{
    /// <summary>Gradi HTML stranicu za Tester (OpenType značajke + varijabilne osi) u WebView2.</summary>
    public static class TesterPage
    {
        public class Prepared
        {
            public string Html;
            public List<string> Files = new List<string>();
        }

        public static Prepared Build(FontItem f, string text, double size, Dictionary<string, string> colors)
        {
            var p = new Prepared();
            var inv = CultureInfo.InvariantCulture;

            // Datoteka za svaki stil
            var styleFiles = new List<(FontStyleItem style, string file)>();
            foreach (var s in f.Styles)
            {
                string file = s.FilePath;
                if (file == null)
                {
                    var g = FontAnalyzer.GetGlyphTypeface(f, s);
                    file = g != null ? FontAnalyzer.FileOf(g) : null;
                }
                file ??= f.MainFile;
                styleFiles.Add((s, file));
            }

            var allFiles = styleFiles.Select(x => x.file).Where(x => x != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var info = new OpenTypeInfo();
            var featureSet = new HashSet<string>();
            foreach (var file in allFiles.Take(6))
            {
                var i = OpenTypeReader.Read(file);
                foreach (var ft in i.Features) featureSet.Add(ft);
                if (info.Axes.Count == 0 && i.Axes.Count > 0) info.Axes = i.Axes;
            }
            bool variable = info.Axes.Count > 0;
            bool hasCollection = allFiles.Any(x => x.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".otc", StringComparison.OrdinalIgnoreCase));
            bool useLocalName = allFiles.Count == 0 || (hasCollection && f.IsSystem);

            var css = new StringBuilder();
            const string fam = "FSTest";
            var styles = new List<object>();
            if (!useLocalName)
            {
                var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (s, file) in styleFiles)
                {
                    if (file == null) continue;
                    int idx = p.Files.FindIndex(x => string.Equals(x, file, StringComparison.OrdinalIgnoreCase));
                    if (idx < 0) { p.Files.Add(file); idx = p.Files.Count - 1; }
                    var key = variable ? file + (s.IsItalic ? "|i" : "") : file + "|" + s.WeightValue + "|" + s.IsItalic;
                    if (emitted.Add(key))
                    {
                        var weight = variable ? "1 1000" : s.WeightValue.ToString(inv);
                        css.Append($"@font-face{{font-family:\"{fam}\";src:url(\"https://fontshelf.local/font/{idx}\");" +
                                   $"font-weight:{weight};font-style:{(s.IsItalic ? "italic" : "normal")};" +
                                   (variable ? "font-stretch:25% 200%;" : "") + "font-display:block;}\n");
                    }
                }
            }
            foreach (var s in f.Styles)
                styles.Add(new { name = s.Name, weight = s.WeightValue, italic = s.IsItalic });

            var features = featureSet.Where(OpenTypeReader.IsUserFeature)
                .OrderBy(t => t.StartsWith("ss") ? 2 : t.StartsWith("cv") ? 3 : 1)
                .ThenBy(t => t)
                .Select(t => new { tag = t, label = OpenTypeReader.FeatureLabel(t), on = OpenTypeReader.DefaultOn(t) })
                .ToList();

            var cfg = new
            {
                family = useLocalName ? "\"" + f.Name.Replace("\"", "") + "\"" : "\"" + fam + "\"",
                text,
                size = Math.Round(size),
                styles,
                features,
                axes = info.Axes.Select(a => new { tag = a.Tag, name = a.Name, min = a.Min, def = a.Default, max = a.Max }).ToList(),
                colors
            };

            var json = JsonSerializer.Serialize(cfg);
            p.Html = Template.Replace("/*FONTFACES*/", css.ToString()).Replace("/*CFG*/", json);
            return p;
        }

        private const string Template = @"<!doctype html>
<html><head><meta charset=""utf-8"">
<style>
/*FONTFACES*/
*{box-sizing:border-box}
body{margin:0;font-family:'Segoe UI',sans-serif;font-size:13px;user-select:none}
.wrap{padding:6px 24px 40px}
#sample{outline:none;padding:16px 18px;border-radius:10px;min-height:120px;overflow-wrap:anywhere;line-height:1.2;user-select:text;white-space:pre-wrap;border:1px solid transparent}
#sample:focus{border-color:var(--accent)}
.row{display:flex;align-items:center;gap:10px;margin:8px 0}
.lbl{color:var(--sub);min-width:58px}
h4{color:var(--sub);font-size:11px;letter-spacing:.03em;margin:22px 0 6px;font-weight:600}
label.f{display:flex;align-items:center;gap:9px;padding:5px 0;cursor:pointer}
label.f input{accent-color:var(--accent);width:15px;height:15px;margin:0}
.tag{font-family:Consolas,monospace;color:var(--sub);font-size:11px;margin-left:auto}
input[type=range]{flex:1;accent-color:var(--accent);min-width:60px}
select{background:var(--input);color:var(--text);border:1px solid var(--line);border-radius:7px;padding:5px 8px;font:inherit;flex:1}
.val{width:46px;text-align:right;color:var(--sub);font-variant-numeric:tabular-nums}
button{background:none;border:1px solid var(--line);color:var(--sub);border-radius:7px;padding:3px 9px;cursor:pointer;font:inherit;font-size:12px}
button:hover{color:var(--text);border-color:var(--sub)}
.chips{display:flex;flex-wrap:wrap;gap:6px;margin:10px 0 2px}
.empty{color:var(--sub);padding:4px 0}
.axis .name{min-width:110px}
</style></head>
<body><div class=""wrap"">
<div class=""row""><select id=""style""></select></div>
<div class=""row""><span class=""lbl"">Veličina</span><input type=""range"" id=""size"" min=""10"" max=""220""><span class=""val"" id=""sizev""></span></div>
<div class=""row""><span class=""lbl"">Prored</span><input type=""range"" id=""lh"" min=""0.8"" max=""2.4"" step=""0.05"" value=""1.2""><span class=""val"" id=""lhv""></span></div>
<div class=""row""><span class=""lbl"">Razmak</span><input type=""range"" id=""ls"" min=""-0.1"" max=""0.5"" step=""0.005"" value=""0""><span class=""val"" id=""lsv""></span></div>
<div class=""chips"" id=""samples""></div>
<div id=""sample"" contenteditable=""true"" spellcheck=""false""></div>
<div id=""axes""></div>
<div id=""feats""></div>
</div>
<script>
const cfg = /*CFG*/;
const $ = id => document.getElementById(id);
const root = document.documentElement.style;
for (const [k,v] of Object.entries(cfg.colors)) root.setProperty('--'+k, v);
document.body.style.background = cfg.colors.bg; document.body.style.color = cfg.colors.text;
const s = $('sample'); s.style.background = cfg.colors.card; s.style.fontFamily = cfg.family + ', ""Segoe UI""';
s.textContent = cfg.text;
const samples = [
  ['Moj tekst', cfg.text],
  ['Ligature', 'fi fl ff ffi ffl ft Th st ct — office, flight, offline'],
  ['Brojke', '0123456789 · 1/2 3/4 5/8 · 1st 2nd · H2O x2 · 1.234,50 €'],
  ['Velika', 'HRVATSKA — ČAROBNI ŽUTI ĐAČIĆ (2026)'],
  ['Odlomak', 'Tipografija je umijeće slaganja slova tako da tekst bude čitljiv, jasan i lijep. Dobar font prenosi ton poruke i prije nego što pročitamo ijednu riječ.'],
  ['Abeceda', 'ABCČĆDĐEFGHIJKLMNOPQRSŠTUVWXYZŽ\nabcčćdđefghijklmnopqrsštuvwxyzž']
];
for (const [n,t] of samples){ const b=document.createElement('button'); b.textContent=n; b.onclick=()=>{s.textContent=t}; $('samples').appendChild(b); }

const sel = $('style');
cfg.styles.forEach((st,i)=>{ const o=document.createElement('option'); o.value=i; o.textContent=st.name; sel.appendChild(o); });
let def = cfg.styles.findIndex(x=>x.weight===400 && !x.italic); if (def<0) def=0; sel.value=def;
if (cfg.styles.length<=1) sel.parentElement.style.display='none';
$('size').value = cfg.size;

const axisState = {};
if (cfg.axes.length){
  const h=document.createElement('h4'); h.textContent='VARIJABILNE OSI'; $('axes').appendChild(h);
  for (const a of cfg.axes){
    axisState[a.tag]=a.def;
    const r=document.createElement('div'); r.className='row axis';
    const step = (a.max-a.min)>50 ? 1 : 0.01;
    r.innerHTML = `<span class=""name"">${a.name} <span class=""tag"" style=""margin-left:4px"">${a.tag}</span></span><input type=""range"" min=""${a.min}"" max=""${a.max}"" step=""${step}"" value=""${a.def}""><span class=""val""></span><button title=""Vrati na zadano"">↺</button>`;
    const inp=r.querySelector('input'), val=r.querySelector('.val'), btn=r.querySelector('button');
    const upd=()=>{ axisState[a.tag]=parseFloat(inp.value); val.textContent=(+inp.value).toFixed(step<1?2:0); apply(); };
    inp.oninput=upd; btn.onclick=()=>{inp.value=a.def; upd();};
    val.textContent=(+a.def).toFixed(step<1?2:0);
    $('axes').appendChild(r);
  }
}

const featState = {};
{
  const h=document.createElement('h4'); h.textContent='OPENTYPE ZNAČAJKE'; $('feats').appendChild(h);
  if (!cfg.features.length){ const e=document.createElement('div'); e.className='empty'; e.textContent='Ovaj font nema dodatnih OpenType značajki.'; $('feats').appendChild(e); }
  for (const f of cfg.features){
    featState[f.tag]=f.on;
    const l=document.createElement('label'); l.className='f';
    l.innerHTML=`<input type=""checkbox"" ${f.on?'checked':''}><span>${f.label}</span><span class=""tag"">${f.tag}</span>`;
    l.querySelector('input').onchange=e=>{ featState[f.tag]=e.target.checked; apply(); };
    $('feats').appendChild(l);
  }
}

function apply(){
  const st = cfg.styles[sel.value] || {weight:400, italic:false};
  const size = $('size').value; $('sizev').textContent = size + ' pt';
  const lh = $('lh').value; $('lhv').textContent = (+lh).toFixed(2);
  const ls = $('ls').value; $('lsv').textContent = (+ls).toFixed(3);
  s.style.fontSize = size + 'px';
  s.style.lineHeight = lh;
  s.style.letterSpacing = ls + 'em';
  s.style.fontWeight = st.weight;
  s.style.fontStyle = st.italic ? 'italic' : 'normal';
  const ff = Object.entries(featState).map(([t,on])=>`""${t}"" ${on?1:0}`);
  s.style.fontFeatureSettings = ff.length ? ff.join(', ') : 'normal';
  const fv = Object.entries(axisState).map(([t,v])=>`""${t}"" ${v}`);
  s.style.fontVariationSettings = fv.length ? fv.join(', ') : 'normal';
}
sel.onchange = () => {
  const st = cfg.styles[sel.value];
  if (st && cfg.axes.length){
    const w = cfg.axes.find(a=>a.tag==='wght');
    if (w){ axisState.wght = Math.min(w.max, Math.max(w.min, st.weight)); const inp=[...document.querySelectorAll('.axis')].find(r=>r.innerHTML.includes('>wght<')); if (inp){ inp.querySelector('input').value=axisState.wght; inp.querySelector('.val').textContent=axisState.wght; } }
  }
  apply();
};
for (const id of ['size','lh','ls']) $(id).oninput = apply;
apply();
</script>
</body></html>";
    }
}
