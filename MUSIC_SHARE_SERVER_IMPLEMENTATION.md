# Music Share Server - Implementation Documentation

## Overview

The Music Share server has been fully implemented as part of the existing Flask application. It provides real-time music metadata and audio streaming capabilities using Redis for session management and buffering.

## Architecture

### Components

1. **music_share.py** - Core Music Share functionality
   - `MusicShareManager` class handles all session operations
   - Redis-based session state management
   - Audio buffering using Redis Sorted Sets
   - Metadata storage with automatic TTL

2. **app.py** - Flask API endpoints
   - 7 Music Share endpoints added
   - Shared authentication with existing YouTube fetching
   - Error handling and logging integration

3. **Redis** - Data store
   - Session state storage
   - Metadata caching
   - Audio chunk buffering
   - Automatic expiration and cleanup

## Deployment

### Docker Setup

The server runs in Docker with the following services:

1. **songrequests** - Flask application
2. **songrequests-database** - PostgreSQL (existing)
3. **redis** - New Redis service for Music Share

### Starting the Server

```bash
# Build and start all services
docker-compose up --build

# Stop services
docker-compose down

# View logs
docker-compose logs -f songrequests
docker-compose logs -f redis
```

### Redis Configuration

- **Image**: `redis:7-alpine`
- **Memory Limit**: 256MB with LRU eviction policy
- **Persistence**: AOF (Append Only File) enabled
- **Health Check**: Ping every 5 seconds
- **Volume**: `redis_data` for persistence

## API Endpoints

All endpoints require the same `Authorization` header as YouTube fetching:
```
Authorization: Bearer Token
```

### 1. POST `/api/share/start`

Start a new Music Share session.

**Request:**
```json
{
  "sessionId": "123456"
}
```

**Response (200):**
```json
{
  "success": true,
  "sessionId": "123456",
  "timestamp": 1234567890123
}
```

**Response (409) - Session already exists:**
```json
{
  "success": false,
  "error": "Session ID already in use"
}
```

### 2. POST `/api/share/{sessionId}/metadata`

Send metadata update for a session.

**Request:**
```json
{
  "title": "Song Title",
  "artist": "Artist Name",
  "lyrics": "[00:12.50]Line 1",
  "hasSyncedLyrics": true,
  "elapsedSeconds": 45.2,
  "totalSeconds": 180.5,
  "thumbnailData": "base64...",
  "timestamp": 1234567890123
}
```

**Response (200):**
```json
{
  "success": true,
  "listenersNotified": 3
}
```

### 3. POST `/api/share/{sessionId}/audio`

Send audio chunk.

**Request:**
```json
{
  "data": "base64_encoded_float_array",
  "sampleRate": 44100,
  "channels": 2,
  "timestamp": 1234567890123,
  "sequenceNumber": 42
}
```

**Response (200):**
```json
{
  "success": true,
  "buffered": true
}
```

### 4. POST `/api/share/{sessionId}/stop`

Stop a session.

**Response (200):**
```json
{
  "success": true,
  "sessionDuration": 125.3,
  "bytesTransferred": 22118400
}
```

### 5. GET `/api/share/{sessionId}/status`

Check session status.

**Response (200):**
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

### 6. GET `/api/share/{sessionId}/metadata`

Get latest metadata (receiver).

**Response (200):**
```json
{
  "title": "Song Title",
  "artist": "Artist Name",
  "lyrics": "[00:12.50]Line 1",
  "hasSyncedLyrics": true,
  "elapsedSeconds": 45.2,
  "totalSeconds": 180.5,
  "thumbnailData": "base64...",
  "timestamp": 1234567890123
}
```

**Response (404):**
```json
{
  "error": "Session not found or no metadata available"
}
```

### 7. GET `/api/share/{sessionId}/audio?since={sequenceNumber}`

Get audio chunks (receiver).

**Query Parameters:**
- `since`: Last received sequence number (-1 for initial)

**Response (200):**
```json
[
  {
    "data": "base64...",
    "sampleRate": 44100,
    "channels": 2,
    "timestamp": 1234567890123,
    "sequenceNumber": 43
  }
]
```

**Response (204):** No new chunks available (empty response)

**Response (404):**
```json
{
  "error": "Session not found"
}
```

## Redis Data Schema

### Session State
```
Key: session:{sessionId}
Type: Hash
TTL: 900 seconds (15 minutes)

Fields:
- active: "true"/"false"
- startTime: "1234567890123"
- lastActivity: "1234567890123"
- listenerCount: "5"
```

### Metadata
```
Key: session:{sessionId}:metadata
Type: Hash
TTL: 30 seconds

Fields:
- title, artist, lyrics
- hasSyncedLyrics: "true"/"false"
- elapsedSeconds, totalSeconds
- thumbnailData: base64 string
- timestamp: "1234567890123"
```

### Audio Buffer
```
Key: session:{sessionId}:audio
Type: Sorted Set (ZSET)
TTL: 10 seconds

Members: JSON-encoded audio chunks
Score: Sequence number

Sorted by sequence number for ordered retrieval
Maximum 50 chunks (~5 seconds of audio)
```

## Features

### Session Management
- ✅ 6-digit session ID validation
- ✅ Collision detection (409 Conflict)
- ✅ Maximum 50 concurrent sessions
- ✅ Automatic timeout after 15 minutes inactivity
- ✅ Graceful shutdown with 5-minute grace period

### Audio Buffering
- ✅ Redis Sorted Set for ordered chunks
- ✅ 5-second circular buffer (50 chunks)
- ✅ Automatic old chunk removal (LRU)
- ✅ Sequence number-based ordering
- ✅ Efficient range queries

### Metadata Handling
- ✅ 30-second TTL for freshness
- ✅ Support for synced lyrics
- ✅ Base64 thumbnail support (JPEG)
- ✅ Timestamp-based sync
- ✅ Automatic expiration

### Authentication
- ✅ Shared bearer token with YouTube fetching
- ✅ Same `Authorization` header format
- ✅ Consistent with existing `/fetch` endpoints

### Error Handling
- ✅ Proper HTTP status codes
- ✅ Descriptive error messages
- ✅ Logging integration
- ✅ Graceful degradation

### Performance
- ✅ Redis for low-latency access
- ✅ Efficient sorted set operations
- ✅ Automatic memory management (256MB limit)
- ✅ LRU eviction policy
- ✅ Connection pooling

## Configuration

### config.py Settings

```python
# Music Share Configuration
REDIS_URL = 'redis://redis:6379'  # Redis connection URL
MAX_SESSIONS = 50  # Maximum concurrent sessions
```

### Environment Variables

All configuration is in `config.py`. To override:

1. **Redis URL**: Modify `REDIS_URL` in `config.py`
2. **Max Sessions**: Modify `MAX_SESSIONS` in `config.py`
3. **Bearer Token**: Already configured as `BEARER_TOKEN`

## Monitoring

### Health Checks

- **Flask App**: `GET /health` (every 5 seconds)
- **Redis**: `redis-cli ping` (every 5 seconds)
- **Database**: `pg_isready` (every 5 seconds)

### Logging

Music Share events are logged via the existing `logs_engine`:

```python
logs.error(f"Error starting Music Share session: {e}")
logs.error(f"Error sending metadata: {e}")
logs.error(f"Error sending audio chunk: {e}")
```

### Metrics to Monitor

1. **Active Sessions**: Call `music_share.get_active_session_count()`
2. **Redis Memory**: `docker exec -it redis redis-cli INFO memory`
3. **Error Rate**: Check application logs
4. **API Response Times**: Monitor Flask logs

## Testing

### Manual Testing

1. **Start Session:**
```bash
curl -X POST http://localhost:5000/api/share/start \
  -H "Authorization: Bearer Token" \
  -H "Content-Type: application/json" \
  -d '{"sessionId": "123456"}'
```

2. **Send Metadata:**
```bash
curl -X POST http://localhost:5000/api/share/123456/metadata \
  -H "Authorization: Bearer Token" \
  -H "Content-Type: application/json" \
  -d '{"title": "Test Song", "artist": "Test Artist", "timestamp": 1234567890123}'
```

3. **Check Status:**
```bash
curl -X GET http://localhost:5000/api/share/123456/status \
  -H "Authorization: Bearer Token"
```

4. **Get Metadata:**
```bash
curl -X GET http://localhost:5000/api/share/123456/metadata \
  -H "Authorization: Bearer Token"
```

5. **Stop Session:**
```bash
curl -X POST http://localhost:5000/api/share/123456/stop \
  -H "Authorization: Bearer Token"
```

### Redis Inspection

```bash
# Connect to Redis
docker exec -it <redis_container> redis-cli

# List all keys
KEYS *

# Get session data
HGETALL session:123456

# Get metadata
HGETALL session:123456:metadata

# Get audio buffer size
ZCARD session:123456:audio

# Get audio chunks
ZRANGE session:123456:audio 0 -1
```

## Troubleshooting

### Connection Refused to Redis

**Error:** `ConnectionError: Error connecting to Redis`

**Solution:**
1. Check Redis container is running: `docker-compose ps`
2. Check Redis health: `docker-compose logs redis`
3. Restart services: `docker-compose restart`

### Session Not Found

**Error:** `404 {"error": "Session not found"}`

**Causes:**
1. Session expired (15 minutes inactivity)
2. Invalid session ID format
3. Session was stopped

**Solution:**
1. Create new session with `POST /api/share/start`
2. Validate session ID is 6 digits
3. Check session status first

### Out of Memory

**Error:** Redis evicting keys unexpectedly

**Solution:**
1. Increase Redis memory limit in docker-compose.yaml
2. Reduce `MAX_SESSIONS` in config.py
3. Monitor active sessions
4. Decrease audio buffer size (modify `music_share.py`)

### High Latency

**Symptoms:** Audio stuttering, delayed metadata updates

**Solutions:**
1. Check network bandwidth
2. Monitor Redis CPU usage
3. Reduce polling frequency on client
4. Use compression (future enhancement)

## Security Considerations

1. **Authentication**: Same bearer token as existing API
2. **Rate Limiting**: Not implemented (future enhancement)
3. **Input Validation**: Session ID format validated
4. **Resource Limits**: 
   - Max 50 sessions
   - 256MB Redis memory
   - 50 audio chunks per session
5. **Automatic Cleanup**: 15-minute timeout

## Future Enhancements

1. **WebSocket Support**: Real-time push instead of polling
2. **Audio Compression**: Opus codec integration
3. **Rate Limiting**: Per-session request throttling
4. **Analytics**: Track session statistics
5. **Multi-Region**: Distribute across multiple Redis instances
6. **Persistent Recording**: Save sessions to disk

## Maintenance

### Backup

Redis uses AOF persistence. Data is in `redis_data` Docker volume:

```bash
# Backup
docker run --rm -v songrequestdesktopv2rewrite_redis_data:/data -v $(pwd):/backup alpine tar czf /backup/redis_backup.tar.gz -C /data .

# Restore
docker run --rm -v songrequestdesktopv2rewrite_redis_data:/data -v $(pwd):/backup alpine tar xzf /backup/redis_backup.tar.gz -C /data
```

### Cleanup

```bash
# Remove all Docker volumes (CAUTION: deletes all data)
docker-compose down -v

# Remove only Redis data
docker volume rm songrequestdesktopv2rewrite_redis_data
```

## Changelog

### Version 1.0 (Initial Implementation)

- ✅ Complete Music Share API endpoints
- ✅ Redis integration
- ✅ Session management
- ✅ Audio buffering
- ✅ Metadata handling
- ✅ Shared authentication
- ✅ Docker deployment
- ✅ Health checks
- ✅ Error handling
- ✅ Logging integration

---

**Documentation Version**: 1.0  
**Last Updated**: 2024  
**Author**: SongRequest Development Team
