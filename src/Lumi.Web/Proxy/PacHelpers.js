// PAC 標準輔助函式（Lumi 自行撰寫）。dnsResolve、myIpAddress 由 C# 提供。
// 注意：這個檔案會以內嵌資源 Lumi.Web.Proxy.PacHelpers.js 發佈。

function isPlainHostName(host) {
    return host.indexOf('.') === -1;
}

function dnsDomainIs(host, domain) {
    return host.length >= domain.length && host.substring(host.length - domain.length) === domain;
}

function localHostOrDomainIs(host, hostdom) {
    return host === hostdom || (host.indexOf('.') === -1 && hostdom.indexOf(host + '.') === 0);
}

function isResolvable(host) {
    return dnsResolve(host) != null;
}

function dnsDomainLevels(host) {
    var count = 0;
    for (var i = 0; i < host.length; i++) {
        if (host.charAt(i) === '.') count++;
    }
    return count;
}

function shExpMatch(str, shexp) {
    var re = '^';
    for (var i = 0; i < shexp.length; i++) {
        var c = shexp.charAt(i);
        if (c === '*') re += '.*';
        else if (c === '?') re += '.';
        else if ('\\^$+.()|{}[]'.indexOf(c) !== -1) re += '\\' + c;
        else re += c;
    }
    re += '$';
    return new RegExp(re).test(str);
}

function __lumiIpToNumber(ip) {
    var m = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(ip);
    if (!m) return null;
    var n = 0;
    for (var i = 1; i <= 4; i++) {
        var part = parseInt(m[i], 10);
        if (part > 255) return null;
        n = n * 256 + part;
    }
    return n;
}

function isInNet(host, pattern, mask) {
    var ip = __lumiIpToNumber(host);
    if (ip === null) {
        var resolved = dnsResolve(host);
        if (resolved == null) return false;
        ip = __lumiIpToNumber(resolved);
        if (ip === null) return false;
    }
    var p = __lumiIpToNumber(pattern);
    var m = __lumiIpToNumber(mask);
    if (p === null || m === null) return false;
    // 逐位元 AND：拆成高低 16 位元，避開 JavaScript 位元運算的 32 位元有號數問題
    var ipHi = Math.floor(ip / 65536), ipLo = ip % 65536;
    var pHi = Math.floor(p / 65536), pLo = p % 65536;
    var mHi = Math.floor(m / 65536), mLo = m % 65536;
    return (ipHi & mHi) === (pHi & mHi) && (ipLo & mLo) === (pLo & mLo);
}

// 時間相關的函式一律回 false（Lumi 的判斷不依賴時間）
function weekdayRange() { return false; }
function dateRange() { return false; }
function timeRange() { return false; }
