using UnityEngine;
using UnityEngine.SceneManagement;

/// Grid-based block stacking game (2D gameplay in a 3D project).
/// Attach this to an empty GameObject. Everything else is built at runtime.
/// Blocks are spawned manually, and only one moving block can exist at a time.
public class StackGame : MonoBehaviour
{
    [Header("Grid")]
    public int columns = 9;        // width of the stage in cells
    public int startWidth = 4;     // width of the first row in cells

    [Header("Movement")]
    public float startSpeed = 4f;      // cells per second
    public float speedIncrease = 0.15f;
    public float maxSpeed = 12f;

    [Header("Input")]
    public KeyCode stopKey = KeyCode.Space;    // also: left mouse button
    public KeyCode spawnKey = KeyCode.Return;  // also: right mouse button

    [Header("Camera")]
    public Camera cam;
    public float cameraFollowSpeed = 3f;

    Transform movingRow;   // null = no moving block exists
    int rowIndex;          // index of the next/current moving row
    int prevStart;         // first cell of the last placed row
    int prevWidth;         // width of the last placed row
    int width;             // width of the moving row
    float pos;             // left edge of the moving row (in cells)
    float speed;
    float dir = 1f;        // starts moving left -> right
    bool gameOver;

    void Start()
    {
        if (cam == null) cam = Camera.main;

        // 2D look: orthographic camera looking straight down the Z axis
        cam.orthographic = true;
        cam.orthographicSize = Mathf.Max(7f, columns * 0.6f / cam.aspect);
        cam.transform.rotation = Quaternion.identity;
        cam.transform.position = new Vector3((columns - 1) / 2f, 5f, -10f);

        speed = startSpeed;
        width = startWidth;

        // Base row (static)
        prevWidth = startWidth;
        prevStart = (columns - startWidth) / 2;
        BuildRow(prevWidth, 0, prevStart);

        rowIndex = 1;
        // No moving block yet: wait for the spawn input
    }

    void Update()
    {
        if (gameOver)
        {
            if (StopPressed() || SpawnPressed())
                SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
            return;
        }

        if (movingRow == null)
        {
            // Nothing is moving: allow a spawn
            if (SpawnPressed()) SpawnBlock();
        }
        else
        {
            // Move the top row back and forth
            pos += dir * speed * Time.deltaTime;
            float max = columns - width;
            if (pos > max) { pos = max; dir = -1f; }
            else if (pos < 0f) { pos = 0f; dir = 1f; }
            movingRow.position = new Vector3(pos, rowIndex, 0f);

            if (StopPressed()) StopBlock();
        }

        // Camera follows the tower
        Vector3 c = cam.transform.position;
        float targetY = Mathf.Max(5f, rowIndex - 2f);
        c.y = Mathf.Lerp(c.y, targetY, Time.deltaTime * cameraFollowSpeed);
        cam.transform.position = c;
    }

    bool StopPressed()
    {
        return Input.GetKeyDown(stopKey) || Input.GetMouseButtonDown(0);
    }

    bool SpawnPressed()
    {
        return Input.GetKeyDown(spawnKey) || Input.GetMouseButtonDown(1);
    }

    /// Spawns the moving block. Ignored if one already exists.
    /// Can also be hooked to a UI Button's OnClick.
    public void SpawnBlock()
    {
        if (gameOver || movingRow != null) return;

        pos = 0f;
        dir = 1f;
        movingRow = BuildRow(width, rowIndex, 0);
    }

    /// Stops the moving block. Can also be hooked to a UI Button's OnClick.
    public void StopBlock()
    {
        if (gameOver || movingRow == null) return;
        Place();
    }

    void Place()
    {
        int snap = Mathf.RoundToInt(pos);
        int overlapStart = Mathf.Max(snap, prevStart);
        int overlapEnd = Mathf.Min(snap + width, prevStart + prevWidth);
        int overlap = overlapEnd - overlapStart;

        movingRow.position = new Vector3(snap, rowIndex, 0f);

        // Any cell with nothing below it falls off
        for (int i = movingRow.childCount - 1; i >= 0; i--)
        {
            int cell = snap + i;
            if (cell < overlapStart || cell >= overlapEnd)
                Drop(movingRow.GetChild(i));
        }

        if (overlap <= 0)
        {
            gameOver = true;
            return;
        }

        prevStart = overlapStart;
        prevWidth = overlap;
        width = overlap;
        rowIndex++;
        speed = Mathf.Min(speed + speedIncrease, maxSpeed);

        // The row is now placed; there is no moving block until the next spawn
        movingRow = null;
    }

    Transform BuildRow(int cells, int y, int startCell)
    {
        var row = new GameObject("Row " + y).transform;
        row.position = new Vector3(startCell, y, 0f);
        Color color = Color.HSVToRGB((y * 0.07f) % 1f, 0.6f, 0.95f);

        for (int i = 0; i < cells; i++)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.SetParent(row, false);
            cube.transform.localPosition = new Vector3(i, 0f, 0f);
            cube.GetComponent<Renderer>().material.color = color;
            Destroy(cube.GetComponent<Collider>()); // not needed, keeps things simple
        }
        return row;
    }

    void Drop(Transform block)
    {
        block.SetParent(null, true);
        var rb = block.gameObject.AddComponent<Rigidbody>();
        rb.AddTorque(Random.insideUnitSphere * 2f, ForceMode.Impulse);
        Destroy(block.gameObject, 3f);
    }

    void OnGUI()
    {
        var style = new GUIStyle(GUI.skin.label) { fontSize = 32, fontStyle = FontStyle.Bold };
        GUI.Label(new Rect(20, 20, 400, 50), "Score: " + (rowIndex - 1), style);

        var hint = new GUIStyle(GUI.skin.label) { fontSize = 20 };
        if (gameOver)
            GUI.Label(new Rect(20, 70, 600, 50), "Game over - press to restart", style);
        else if (movingRow == null)
            GUI.Label(new Rect(20, 70, 600, 40), "Press " + spawnKey + " to spawn a block", hint);
        else
            GUI.Label(new Rect(20, 70, 600, 40), "Press " + stopKey + " to stop the block", hint);
    }
}
