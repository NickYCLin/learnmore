using System.Text;
using LearnMore.Controllers;
using Microsoft.AspNetCore.Mvc;
using NMeCab;

namespace LearnMore.Controllers.API
{
    [Route("api/[controller]")]
    [ApiController]
    public class MeCabController : ControllerBase
    {
        // GET api/MeCab/GetReading?text=你的日文文字
        [HttpGet("GetReading")]
        public IActionResult GetReading(string text)
        {
            if (!ControllerAccessGuard.IsSignedIn(this))
            {
                return ControllerAccessGuard.LoginRequired(this);
            }

            if (string.IsNullOrEmpty(text))
            {
                return BadRequest("必須傳入要解析的日文文字。");
            }

            try
            {
                var dicPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "ipadic");
                using var tagger = MeCabTagger.Create(dicPath);
                // 使用新版 API，Parse 回傳 IEnumerable<MeCabNode>
                var nodes = tagger.Parse(text);
                var readings = new List<string>();

                foreach (var node in nodes)
                {
                    // 排除 BOS/EOS 節點
                    if (node.Stat != MeCabNodeStat.Nor)
                    {
                        continue;
                    }

                    var featureParts = node.Feature.Split(',');
                    string reading = (featureParts.Length > 7 && !string.IsNullOrEmpty(featureParts[7]))
                                        ? featureParts[7]
                                        : node.Surface;

                    readings.Add(reading);
                }

                var result = string.Join(" ", readings);
                return Ok(new { reading = result });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"處理文字時發生錯誤：{ex.Message}");
            }
        }
    }
}
