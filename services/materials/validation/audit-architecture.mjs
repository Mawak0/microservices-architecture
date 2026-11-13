import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const layers = ['Domain', 'Application', 'Infrastructure', 'Api'];
const allowedReferences = {
  Domain: [],
  Application: ['Domain'],
  Infrastructure: ['Domain', 'Application'],
  Api: ['Domain', 'Application', 'Infrastructure']
};
const forbiddenCoreNamespaces = /^(?:Microsoft\.|System\.(?:Net|Data|Text\.Json|IO)|Npgsql|Amazon|Dapper|MediatR|FluentValidation|ZXing|SixLabors|PdfSharp|QRCoder|Newtonsoft)/;
const forbiddenCoreMechanisms = /\b(?:HttpClient|HttpContext|HttpStatusCode|IServiceProvider|IConfiguration|NpgsqlConnection|SqliteConnection|JsonElement|JsonObject|DbContext)\b/;
const forbiddenAmbientDomain = /\b(?:DateTime|DateTimeOffset)\.(?:Now|UtcNow)|\bGuid\.NewGuid\s*\(|\b(?:File|Directory|Environment)\./;

function filesIn(directory) {
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    if (['bin', 'obj', 'node_modules', '.git'].includes(entry.name)) return [];
    const target = path.join(directory, entry.name);
    return entry.isDirectory() ? filesIn(target) : [target];
  });
}

function namespaceOfImport(line) {
  const match = line.match(/^\s*(?:global\s+)?using\s+(?:static\s+)?(?:\w+\s*=\s*)?([\w.]+)\s*;/);
  return match?.[1];
}

// Preserve line numbers while excluding comments and ordinary string contents.
function withoutCommentsAndStrings(source) {
  return source.replace(/\/\*[\s\S]*?\*\/|\/\/[^\n]*|@"(?:""|[^"])*"|"(?:\\.|[^"\\])*"/g,
    match => match.replace(/[^\n]/g, ' '));
}

export function auditArchitecture(root) {
  const absoluteRoot = path.resolve(root);
  const sources = filesIn(path.join(absoluteRoot, 'src'));
  const projects = sources.filter(file => file.endsWith('.csproj'));
  const failures = [], notes = [], counts = {};
  const projectFor = layer => projects.filter(file => path.basename(file, '.csproj').endsWith(`.${layer}`));
  const fail = (file, message, line) => failures.push({ file: path.relative(absoluteRoot, file), line, message });

  for (const layer of layers) {
    const matches = projectFor(layer);
    if (matches.length !== 1) {
      failures.push({ message: `Expected one ${layer} project; found ${matches.length}.` });
      continue;
    }
    const project = matches[0], projectDirectory = path.dirname(project);
    const xml = fs.readFileSync(project, 'utf8');
    for (const match of xml.matchAll(/<ProjectReference\b[^>]*Include="([^"]+)"/g)) {
      const reference = path.basename(match[1].replaceAll('\\', '/'), '.csproj');
      const referenceLayer = layers.find(value => reference.endsWith(`.${value}`));
      if (!(layer === 'Infrastructure' && ['Olympiad.Persistence.Postgres', 'Olympiad.Storage.S3', 'Olympiad.Workflows.Application'].includes(reference)) && !allowedReferences[layer].includes(referenceLayer))
        fail(project, `${layer} references forbidden project ${reference}.`);
    }
    if (['Domain', 'Application'].includes(layer) && /<(?:PackageReference|FrameworkReference)\b/.test(xml))
      fail(project, `${layer} must not declare external package/framework references.`);
    const codeFiles = sources.filter(file => file.endsWith('.cs') && file.startsWith(projectDirectory + path.sep));
    counts[layer] = { files: codeFiles.length, lines: 0 };
    for (const file of codeFiles) {
      const source = fs.readFileSync(file, 'utf8');
      const stripped = withoutCommentsAndStrings(source);
      const lines = source.split(/\r?\n/);
      counts[layer].lines += lines.length;
      if (['Domain', 'Application'].includes(layer)) {
        lines.forEach((line, index) => {
          const imported = namespaceOfImport(line);
          if (imported && forbiddenCoreNamespaces.test(imported))
            fail(file, `${layer} imports outward technology ${imported}.`, index + 1);
        });
        stripped.split('\n').forEach((line, index) => {
          const mechanism = line.match(forbiddenCoreMechanisms);
          if (mechanism) fail(file, `${layer} uses infrastructure mechanism ${mechanism[0]}.`, index + 1);
          if (layer === 'Domain') {
            const ambient = line.match(forbiddenAmbientDomain);
            if (ambient) fail(file, `Domain obtains nondeterministic/infrastructure value directly: ${ambient[0]}.`, index + 1);
          }
        });
      }
      if (layer === 'Domain') {
        for (const [index, line] of stripped.split('\n').entries()) {
          if (/\bpublic\b[^;\n]*\{\s*get\s*;\s*set\s*;/.test(line))
            fail(file, 'Public mutable domain property bypasses aggregate invariants.', index + 1);
        }
      }
      if (layer === 'Api' && !/^(?:Program|Composition|CompositionRoot|DependencyInjection|ServiceCollectionExtensions|WorkflowConfiguration)\.cs$/.test(path.basename(file))) {
        lines.forEach((line, index) => {
          const imported = namespaceOfImport(line);
          if (imported?.includes('.Infrastructure'))
            fail(file, 'API imports an infrastructure implementation outside composition root.', index + 1);
        });
      }
      if (codeFiles.length && lines.length > 400)
        notes.push({ file: path.relative(absoluteRoot, file), message: `${lines.length} lines: manually inspect cohesion and responsibilities.` });
    }
  }

  // A zero-sized domain cannot satisfy the architecture by folder names alone.
  if (!counts.Domain?.files || !counts.Application?.files)
    failures.push({ message: 'Domain and Application must contain real source.' });
  const inheritedFiles = ['Directory.Build.props', 'Directory.Packages.props'];
  for (const name of inheritedFiles) {
    const file = path.join(absoluteRoot, name);
    if (!fs.existsSync(file)) continue;
    const xml = fs.readFileSync(file, 'utf8');
    if (/<(?:PackageReference|FrameworkReference|ProjectReference)\b/.test(xml))
      fail(file, 'Shared build settings must not inject outward references into Domain/Application.');
  }
  return { root: absoluteRoot, passed: failures.length === 0, counts, failures, notes };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const roots = process.argv.slice(2);
  assert.ok(roots.length, 'Usage: node validation/audit-architecture.mjs <service-root> [...]');
  const reports = roots.map(auditArchitecture);
  console.log(JSON.stringify(reports, null, 2));
  if (reports.some(report => !report.passed)) process.exitCode = 1;
}
