const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const { resolve } = require('node:path');
const { test } = require('node:test');
const vm = require('node:vm');

const source = readFileSync(resolve(__dirname, '../LearnMore/wwwroot/js/site.js'), 'utf8');

function render({ label = '需要人工校正', translations = ['完整翻譯'], noticePresent = true, readyState = 'complete' } = {}) {
    let removed = false;
    let loaded;
    const notice = {
        querySelector(selector) {
            assert.equal(selector, '.badge');
            return label === null ? null : { textContent: label };
        },
        remove() { removed = true; }
    };
    const document = {
        readyState,
        querySelector(selector) {
            assert.equal(selector, '.lyrics-processing-alert');
            return noticePresent ? notice : null;
        },
        querySelectorAll(selector) {
            assert.equal(selector, '.lyrics-line[data-lyric-id][data-timestamp]');
            return translations.map(text => ({
                querySelector(selector) {
                    assert.equal(selector, '.cn-line');
                    return text === null ? null : { textContent: text };
                }
            }));
        },
        addEventListener(event, callback, options) {
            assert.equal(event, 'DOMContentLoaded');
            assert.equal(options.once, true);
            loaded = callback;
        }
    };
    const window = {};
    vm.runInNewContext(source, { document, window });
    return { removed: () => removed, load: () => loaded(), window };
}

test('完整歌詞只隱藏公開頁面的待校正提示', () => {
    assert.equal(render().removed(), true);
});

for (const label of ['高精度排隊中', '高精度處理中', '翻譯待補', '高精度失敗', '秒數待確認', null]) {
    test(`保留其他處理狀態：${label}`, () => {
        assert.equal(render({ label }).removed(), false);
    });
}

for (const translations of [[], [''], ['   '], [null], ['翻譯中...'], ['完整翻譯', '翻譯中...']]) {
    test(`保留沒有歌詞或翻譯不完整的提示：${JSON.stringify(translations)}`, () => {
        assert.equal(render({ translations }).removed(), false);
    });
}

test('後台與其他沒有公開提示框的頁面不受影響', () => {
    assert.equal(render({ noticePresent: false }).removed(), false);
});

test('頁面仍在載入時等 DOMContentLoaded 才移除提示', () => {
    const page = render({ readyState: 'loading' });
    assert.equal(page.removed(), false);
    page.load();
    assert.equal(page.removed(), true);
});

test('保留既有全域 HTML 跳脫功能', () => {
    assert.equal(render().window.escapeHtml('<test>'), '&lt;test&gt;');
});

test('沒有瀏覽器 document 時不執行提示調整', () => {
    assert.doesNotThrow(() => vm.runInNewContext(source, {}));
});
