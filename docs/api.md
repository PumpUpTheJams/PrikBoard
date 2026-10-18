# PrikBoard API guide

This guide is for people and AI agents. The machine-readable contract is the OpenAPI 3.1 document at `/openapi/v1.json`. Point an agent framework or a client generator at it.

- **Base URL (local):** `http://localhost:5080`
- **Format:** JSON with camelCase properties. Enums such as `color` are strings.
- **Authentication:** none yet. Run the API locally only.

## Typical agent workflow

1. `GET /api/boards` (listBoards): find the project.
2. `GET /api/boards/{boardId}` (getBoard): get a full snapshot with every column and note, in order.
3. Act on it:
   - `POST /api/boards/{boardId}/notes` (createNote) adds a task.
   - `POST /api/notes/{noteId}/move` (moveNote) moves a task, for example from *Doing* to *Done*.
   - `PATCH /api/notes/{noteId}` (updateNote) edits the title, body or colour.

```bash
# Create a board (starts with To do / Doing / Done)
curl -X POST localhost:5080/api/boards -H "Content-Type: application/json" \
  -d '{"name":"Website relaunch"}'

# Add a note to the first column
curl -X POST localhost:5080/api/boards/{boardId}/notes -H "Content-Type: application/json" \
  -d '{"title":"Pick a colour palette","color":"Pink"}'

# Move it to another column (append at the bottom)
curl -X POST localhost:5080/api/notes/{noteId}/move -H "Content-Type: application/json" \
  -d '{"columnId":"{doneColumnId}"}'
```

## Endpoints

| operationId | Method and path | Purpose |
| --- | --- | --- |
| listBoards | `GET /api/boards` | All boards, newest first, without notes |
| createBoard | `POST /api/boards` | New board with default columns |
| getBoard | `GET /api/boards/{boardId}` | Full snapshot: columns and notes in order |
| updateBoard | `PATCH /api/boards/{boardId}` | Rename or edit the description |
| deleteBoard | `DELETE /api/boards/{boardId}` | Delete the board with all columns and notes |
| createColumn | `POST /api/boards/{boardId}/columns` | Add a column, optionally at a position |
| updateColumn | `PATCH /api/columns/{columnId}` | Rename or reorder a column |
| deleteColumn | `DELETE /api/columns/{columnId}` | Delete an empty column (409 if it has notes) |
| listNotes | `GET /api/boards/{boardId}/notes?columnId=&search=` | Filter by column, or search title and body (case-insensitive) |
| createNote | `POST /api/boards/{boardId}/notes` | New note. Defaults to the first column, at the bottom, Yellow |
| getNote | `GET /api/notes/{noteId}` | A single note |
| updateNote | `PATCH /api/notes/{noteId}` | Edit the title, body or colour |
| moveNote | `POST /api/notes/{noteId}/move` | Move to a column on the same board, or reorder within a column |
| deleteNote | `DELETE /api/notes/{noteId}` | Delete a note |

`GET /health` returns `Healthy` when the API is up.

## Conventions

- **Positions** are zero-based, left to right for columns and top to bottom for notes. Leave out `position` to append at the end. Out-of-range values are clamped.
- **PATCH** only changes the fields you send. Sending `""` for an optional text field (`description`, `body`) clears it.
- **Note colours** are `Yellow`, `Pink`, `Blue`, `Green`, `Orange` and `Purple`.
- **Errors** use `application/problem+json`. Validation errors (400) list the offending fields under `errors`. A 404 means the resource does not exist. A 409 means the request conflicts with the current state, for example deleting a column that still has notes.
