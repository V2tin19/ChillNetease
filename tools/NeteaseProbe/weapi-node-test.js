const crypto = require('crypto');
const https = require('https');
const AesKey = Buffer.from('0CoJUm6Qyw8W8jud');
const AesIv = Buffer.from('0102030405060708');
const ModulusHex = 'e0b509f6259df8642dbc35662901477df22677ec152b5ff68ace615bb7b725152b3ab17a876aea8a5aa76d2e417629ec4ee341f56135fccf695280104e0312ecbda92557c93870114af6c9d05c4f7f0c3685b7a46bee255932575cce10b424d813cfe4875d3e82047b97ddef52741d546b8e289dc6935b3ece0462db0a22b8e7';
const PubExp = 65537n;
function aesEncrypt(data, key) {
  const cipher = crypto.createCipheriv('aes-128-cbc', key, AesIv);
  return Buffer.concat([cipher.update(data), cipher.final()]);
}
function pkcs1Pad(data) {
  const k = 128; const padLen = k - data.length - 3;
  const buf = Buffer.alloc(k);
  buf[0] = 0; buf[1] = 1; buf.fill(0xff, 2, 2 + padLen); buf[2 + padLen] = 0;
  data.copy(buf, 2 + padLen + 1); return buf;
}
function rsaEncrypt(data) {
  const m = BigInt('0x' + pkcs1Pad(data).toString('hex'));
  const n = BigInt('0x' + ModulusHex);
  return (m ** PubExp % n).toString(16).padStart(256, '0');
}
function weapi(payloadJson) {
  const secKey = crypto.randomBytes(16);
  const once = aesEncrypt(Buffer.from(payloadJson), AesKey);
  const twice = aesEncrypt(once, secKey);
  const reversed = Buffer.from(secKey).reverse();
  return { params: twice.toString('base64'), encSecKey: rsaEncrypt(reversed) };
}
// 从 go-musicfox cookie 文件读基础 cookie
const fs = require('fs');
const cookieFile = process.env.LOCALAPPDATA + '/go-musicfox/cookie';
const kv = {};
for (const ln of fs.readFileSync(cookieFile, 'utf8').split('\n')) {
  if (ln.startsWith('#') || !ln.trim()) continue;
  const p = ln.split('\t');
  if (p.length >= 7) kv[p[5]] = p[6];
}
const cookie = Object.entries(kv).map(([k, v]) => k + '=' + v).join('; ');
console.log('使用 cookie 键:', Object.keys(kv).join(','));

function post(path, payload, withCookie) {
  const r = weapi(JSON.stringify(payload));
  const body = 'params=' + encodeURIComponent(r.params) + '&encSecKey=' + encodeURIComponent(r.encSecKey);
  const hdrs = {
    'Content-Type': 'application/x-www-form-urlencoded',
    'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36',
    'Referer': 'https://music.163.com/', 'Origin': 'https://music.163.com',
    'Content-Length': Buffer.byteLength(body)
  };
  if (withCookie) hdrs['Cookie'] = cookie;
  const req = https.request({ host: 'music.163.com', path, method: 'POST', headers: hdrs }, (res) => {
    let data = '';
    res.on('data', c => data += c);
    res.on('end', () => console.log(path, withCookie?'[带cookie]':'[无cookie]', '→', res.statusCode, '|', data.slice(0, 200)));
  });
  req.on('error', e => console.log(path, 'ERR', e.message));
  req.write(body); req.end();
}
post('/weapi/login/status', {}, true);
setTimeout(() => post('/weapi/login/status', {}, false), 1200);
