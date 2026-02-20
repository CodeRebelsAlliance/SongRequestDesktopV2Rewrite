# Music Share Server Component - Technical Specification

## Overview

The Music Share server acts as a relay between sharers and receivers, handling real-time music metadata and audio streaming. It must handle multiple concurrent sessions with low latency and high reliability.

## Technology Stack Recommendations

- **Backend Framework**: Node.js (Express) or Python (FastAPI) or ASP.NET Core
- **Real-time Communication**: WebSockets or Server-Sent Events (SSE)
- **Data Storage**: Redis (for session state and buffers)
- **Audio Streaming**: Binary chunked transfer
- **Authentication**: Same bearer token system as YouTube fetching (shared authentication)

## API Endpoints

### Base URL
```
http://127.0.0.1:5000
```

---

### 1. Start Sharing Session

**Endpoint**: `POST /api/share/start`

**Headers**:
```
Authorization: Bearer Token
Content-Type: application/json
```

**Note**: The `Authorization` header value comes from the application's configuration (`BearerToken` setting), which is the same token used for YouTube fetching. The server should validate this token using the same authentication system.

**Request Body**:
```json
{
  "sessionId": "123456"
}
```

**Response** (200 OK):
```json
{
  "success": true,
  "sessionId": "123456",
  "timestamp": 1234567890123
}
```

**Response** (409 Conflict) - Session ID already exists:
```json
{
  "success": false,
  "error": "Session ID already in use"
}
```

**Purpose**: Registers a new sharing session and allocates server resources.

**Server Actions**:
- Validate session ID format (6 digits)
- Check if session ID is already active
- Create session state in Redis
- Initialize audio buffer for this session
- Set session timeout (15 minutes of inactivity)

---

### 2. Send Metadata Update

**Endpoint**: `POST /api/share/{sessionId}/metadata`

**Headers**:
```
Authorization: Bearer Token
Content-Type: application/json
```

**Request Body**:
```json
{
  "title": "Song Title",
  "artist": "Artist Name",
  "lyrics": "[00:12.50]Lyrics line 1\n[00:15.80]Lyrics line 2",
  "hasSyncedLyrics": true,
  "elapsedSeconds": 45.2,
  "totalSeconds": 180.5,
  "thumbnailData": "base64_encoded_jpeg_data_or_null",
  "timestamp": 1234567890123
}
```

**Response** (200 OK):
```json
{
  "success": true,
  "listenersNotified": 3
}
```

**Purpose**: Updates session metadata for all connected receivers.

**Server Actions**:
- Validate session exists and is active
- Store metadata in Redis with 30-second TTL
- Broadcast update to all receivers via WebSocket/SSE
- Update last activity timestamp

---

### 3. Send Audio Chunk

**Endpoint**: `POST /api/share/{sessionId}/audio`

**Headers**:
```
Authorization: Bearer Token
Content-Type: application/json
```

**Request Body**:
```json
{
  "data": "base64_encoded_float_array",
  "sampleRate": 44100,
  "channels": 2,
  "timestamp": 1234567890123,
  "sequenceNumber": 42
}
```

**Response** (200 OK):
```json
{
  "success": true,
  "buffered": true
}
```

**Purpose**: Streams audio data from sharer to server buffer.

**Server Actions**:
- Validate session exists
- Decode base64 audio data
- Store in circular buffer (Redis Stream or in-memory queue)
- Buffer should hold ~5 seconds of audio
- Broadcast availability to receivers
- Discard old chunks beyond buffer window

**Performance Requirements**:
- **Latency**: <50ms processing time
- **Throughput**: ~176KB/s per session (44.1kHz stereo float)
- **Buffer**: 5-second circular buffer per session

---

### 4. Stop Sharing Session

**Endpoint**: `POST /api/share/{sessionId}/stop`

**Headers**:
```
Authorization: Bearer Token
```

**Response** (200 OK):
```json
{
  "success": true,
  "sessionDuration": 125.3,
  "bytesTransferred": 22118400
}
```

**Purpose**: Cleanly ends a sharing session.

**Server Actions**:
- Mark session as inactive
- Notify all receivers via WebSocket
- Clear audio buffer
- Delete session state after 5 minutes (allow late joiners to see "session ended")

---

### 5. Check Session Status

**Endpoint**: `GET /api/share/{sessionId}/status`

**Headers**:
```
Authorization: Bearer Token
```

**Response** (200 OK) - Active session:
```json
{
  "isActive": true,
  "startTime": 1234567890123,
  "listenerCount": 5,
  "currentMetadata": {
    "title": "Song Title",
    "artist": "Artist Name"
  }
}
```

**Response** (404 Not Found):
```json
{
  "isActive": false,
  "error": "Session not found"
}
```

**Purpose**: Verify session exists before attempting to receive.

---

### 6. Get Latest Metadata (Receiver)

**Endpoint**: `GET /api/share/{sessionId}/metadata`

**Headers**:
```
Authorization: Bearer Token
```

**Response** (200 OK):
```json
{
  "title": "Song Title",
  "artist": "Artist Name",
  "lyrics": "[00:12.50]Lyrics line 1",
  "hasSyncedLyrics": true,
  "elapsedSeconds": 45.2,
  "totalSeconds": 180.5,
  "thumbnailData": "base64_jpeg_or_null",
  "timestamp": 1234567890123
}
```

**Response** (404 Not Found):
```json
{
  "error": "Session not found or no metadata available"
}
```

**Purpose**: Poll for latest metadata updates.

**Polling Frequency**: Every 500ms (client-side)

---

### 7. Get Audio Chunks (Receiver)

**Endpoint**: `GET /api/share/{sessionId}/audio?since={sequenceNumber}`

**Headers**:
```
Authorization: Bearer Token
```

**Query Parameters**:
- `since`: Last received sequence number (-1 for initial request)

**Response** (200 OK):
```json
[
  {
    "data": "base64_encoded_float_array",
    "sampleRate": 44100,
    "channels": 2,
    "timestamp": 1234567890123,
    "sequenceNumber": 43
  },
  {
    "data": "base64_encoded_float_array",
    "sampleRate": 44100,
    "channels": 2,
    "timestamp": 1234567890124,
    "sequenceNumber": 44
  }
]
```

**Response** (404 Not Found):
```json
{
  "error": "Session not found"
}
```

**Response** (204 No Content) - No new chunks:
```
(empty response)
```

**Purpose**: Poll for new audio chunks since last received sequence.

**Polling Frequency**: Every 100ms (client-side)

**Server Actions**:
- Query buffer for chunks with sequence > `since`
- Return up to 10 chunks per request
- Mark chunks as delivered for garbage collection

---

## Data Flow Diagrams

### Sharing Mode Flow

```
Client (Sharer)
    |
    v
POST /api/share/start
    |
    v
Server creates session & buffer
    |
    v
<--- Loop every 500ms --->
    |
    v
POST /api/share/{id}/metadata
    |
    v
Server stores & broadcasts
    |
    v
POST /api/share/{id}/audio (continuous)
    |
    v
Server buffers audio chunks
```

### Receiving Mode Flow

```
Client (Receiver)
    |
    v
GET /api/share/{id}/status (validate)
    |
    v
<--- Poll Loop --->
    |
    +---> GET /api/share/{id}/metadata (every 500ms)
    |         |
    |         v
    |     Server returns latest metadata
    |
    +---> GET /api/share/{id}/audio?since=X (every 100ms)
              |
              v
          Server returns buffered chunks
              |
              v
          Client adds to playback buffer
```

---

## Performance Requirements

### Latency
- **Metadata updates**: <500ms end-to-end
- **Audio streaming**: <2 seconds latency (acceptable)
- **API response time**: <50ms per request

### Throughput
- **Per session**: ~176 KB/s audio + ~2 KB/s metadata
- **Server capacity**: Support 50 concurrent sessions = ~8.8 MB/s total
- **Network bandwidth**: 100 Mbps recommended

### Reliability
- **Uptime**: 99.9% availability
- **Data loss**: <0.1% packet loss acceptable (audio glitches)
- **Session recovery**: Auto-cleanup after 15 minutes inactivity

---

## Redis Schema

### Session State
```
Key: session:{sessionId}
Type: Hash
TTL: 900 seconds (15 minutes, refresh on activity)

Fields:
- active: boolean
- startTime: timestamp
- lastActivity: timestamp
- listenerCount: integer
```

### Current Metadata
```
Key: session:{sessionId}:metadata
Type: Hash
TTL: 30 seconds

Fields:
- title: string
- artist: string
- lyrics: string
- hasSyncedLyrics: boolean
- elapsedSeconds: float
- totalSeconds: float
- thumbnailData: base64 string
- timestamp: long
```

### Audio Buffer
```
Key: session:{sessionId}:audio
Type: Redis Stream or List
TTL: 10 seconds (auto-expire old entries)

Entries:
- sequenceNumber: integer (unique, incrementing)
- data: binary (encoded audio samples)
- sampleRate: integer
- channels: integer
- timestamp: long
```

---

## Error Handling

### HTTP Status Codes
- **200 OK**: Success
- **201 Created**: Session created
- **204 No Content**: No new data available
- **400 Bad Request**: Invalid request format
- **401 Unauthorized**: Invalid bearer token
- **404 Not Found**: Session not found
- **409 Conflict**: Session ID already exists
- **500 Internal Server Error**: Server failure
- **503 Service Unavailable**: Server overloaded

### Error Response Format
```json
{
  "success": false,
  "error": "Human-readable error message",
  "code": "ERROR_CODE",
  "timestamp": 1234567890123
}
```

---

## Security Considerations

1. **Authentication**: Validate bearer token on every request using the same authentication system as YouTube fetching
   - Token is configured in application settings (`BearerToken` field)
   - Same token is used for both YouTube fetching and Music Share
   - Server should use shared authentication middleware/logic
   - Example token: `Bearer Token` (configurable)
2. **Rate Limiting**: 
   - Metadata: 2 requests/second per session
   - Audio: 10 requests/second per session
3. **Session Limits**: Max 50 active sessions per server
4. **Data Validation**: 
   - Session ID must be exactly 6 digits
   - Audio chunks max 1MB each
   - Metadata max 5MB (for large thumbnails)
5. **CORS**: Allow requests from localhost:* in development
6. **Timeout**: Auto-cleanup inactive sessions after 15 minutes

---

## Monitoring & Metrics

### Key Metrics
- Active sessions count
- Total audio data transferred (MB/s)
- Listener count per session
- API response times (p50, p95, p99)
- Error rate (%)
- Buffer overflow count
- Redis memory usage

### Logging
Log every:
- Session start/stop
- Connection errors
- Buffer overflows
- Authentication failures

---

## Deployment

### Environment Variables
```
PORT=5000
REDIS_URL=redis://localhost:6379
BEARER_TOKEN=Bearer Token
MAX_SESSIONS=50
AUDIO_BUFFER_SECONDS=5
SESSION_TIMEOUT_MINUTES=15
LOG_LEVEL=info
```

**Note**: `BEARER_TOKEN` must match the token configured in the client application's settings. This is the same token used for YouTube fetching (`/fetch?method=` endpoints). The server should share authentication logic between both features.

### Docker Deployment
```dockerfile
FROM node:18-alpine
WORKDIR /app
COPY package*.json ./
RUN npm install
COPY . .
EXPOSE 5000
CMD ["node", "server.js"]
```

### Health Check
**Endpoint**: `GET /health`

**Response**:
```json
{
  "status": "healthy",
  "activeSessions": 5,
  "uptime": 3600,
  "memoryUsage": {
    "used": 256,
    "total": 512
  }
}
```

---

## Client Implementation Notes

### Audio Chunk Format
- **Data**: IEEE 754 float array (32-bit per sample)
- **Channels**: Interleaved stereo (L, R, L, R, ...)
- **Sample Rate**: 44100 Hz
- **Chunk Size**: ~4096 samples = 93ms of audio

### Base64 Encoding
Audio data must be base64-encoded for JSON transport:
```csharp
byte[] bytes = new byte[samples.Length * sizeof(float)];
Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
string base64 = Convert.ToBase64String(bytes);
```

### Buffering Strategy (Receiver)
1. Start playback after receiving 5 chunks (~465ms buffer)
2. Maintain 2-5 second buffer to handle network jitter
3. If buffer drops below 2 chunks, pause and rebuffer
4. Discard chunks if buffer exceeds 10 seconds (sync correction)

### Synchronization
- Use `timestamp` field to detect clock drift
- Adjust playback rate by ±5% if drift >2 seconds
- Display warning if latency >5 seconds

---

## Testing

### Load Testing
- **Tool**: Apache JMeter or k6
- **Scenarios**:
  1. 10 concurrent sessions, 1 receiver each
  2. 5 concurrent sessions, 10 receivers each
  3. Stress test: 50 sessions with 5 receivers each

### Unit Tests
- Session creation and collision handling
- Audio buffer circular queue logic
- Metadata broadcasting
- Session cleanup on timeout

### Integration Tests
- Full sharer → server → receiver flow
- Network interruption recovery
- Multiple receivers per session

---

## Future Enhancements

1. **WebSocket Support**: Replace polling with WebSocket for lower latency
2. **Audio Compression**: Use Opus codec to reduce bandwidth by 90%
3. **Adaptive Bitrate**: Adjust quality based on network conditions
4. **Recording**: Allow receivers to save shared sessions
5. **Chat**: Add text chat between sharer and receivers
6. **Analytics**: Track listening statistics (duration, popular songs)
7. **Multi-Region**: Deploy servers in multiple regions with CDN

---

## Authentication Integration

Music Share uses the **same authentication system** as YouTube fetching:

### Client-Side
- Token is stored in `ConfigService.Instance.Current.BearerToken`
- Same token is used for both `/fetch` and `/api/share` endpoints
- Server URL is from `ConfigService.Instance.Current.Address`

### Server-Side
- Validate `Authorization` header on all Music Share endpoints
- Use the same authentication middleware as YouTube fetching
- Both features should share authentication logic

### Example Authentication Flow

```
Client Request:
POST /api/share/start
Authorization: Bearer Token

Server Validation:
1. Extract token from Authorization header
2. Validate against configured BEARER_TOKEN
3. Use same validation logic as /fetch endpoints
4. Return 401 Unauthorized if invalid
```

### Configuration

The client application's `config.json` contains:
```json
{
  "BearerToken": "Bearer ",
  "Address": "http://127.0.0.1:5000"
}
```

Both the YouTube fetching and Music Share features read from this shared configuration.

---

## Contact & Support

For implementation questions or clarifications, refer to:
- Client code: `MusicShareService.cs`, `MusicShare.xaml.cs`, `FetchingService.cs`
- Data models: `MusicShareModels.cs`
- Example API client: Provided in client implementation
- Authentication reference: See `FetchingService.cs` for the authentication pattern used

---

**Document Version**: 1.0  
**Last Updated**: 2024  
**Author**: SongRequest Development Team
