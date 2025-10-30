using Microsoft.AspNetCore.Mvc;
using Api.Services.Abstractions;

namespace Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ChatController : ControllerBase
    {
        private readonly IChatHistoryStore _store;

        public ChatController(IChatHistoryStore store)
        {
            _store = store;
        }

        // POST /api/chat/start
        [HttpPost("start")]
        public async Task<IActionResult> StartChat(CancellationToken ct)
        {
            try
            {
                var id = await _store.EnsureThreadAsync(null, ct);
                return Ok(new { conversationId = id });
            }
            catch (Exception ex)
            {
                // Optionally log the exception here, if you have a logger
                return StatusCode(500, new { error = "Failed to start chat", detail = ex.Message });
            }
        }

        // GET /api/chat/{id}/messages
        [HttpGet("{id}/messages")]
        public async Task<IActionResult> GetMessages(string id, CancellationToken ct)
        {
            var msgs = await _store.GetMessagesAsync(id, ct);
            return Ok(msgs);
        }

        // GET /api/chat/list
        [HttpGet("list")]
        public async Task<IActionResult> ListChats([FromQuery] int? take, CancellationToken ct)
        {
            var count = (take ?? 20) <= 0 ? 20 : take!.Value;
            var items = await _store.ListThreadsAsync(count, ct);
            return Ok(items);
        }
    }
}
