using UnityEngine;
using UnityEngine.SceneManagement;

public class SceneChanger : MonoBehaviour
{
    // 인스펙터에서 씬 이름을 입력
    [SerializeField] private string sceneName;

    // 버튼의 OnClick에 연결할 함수
    public void LoadScene()
    {
        SceneManager.LoadScene(sceneName);
    }

    // 씬 이름을 직접 넘기고 싶을 때
    public void LoadSceneByName(string name)
    {
        SceneManager.LoadScene(name);
    }

    // 빌드 인덱스로 이동
    public void LoadSceneByIndex(int index)
    {
        SceneManager.LoadScene(index);
    }

    // 게임 종료 (보너스)
    public void QuitGame()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
}