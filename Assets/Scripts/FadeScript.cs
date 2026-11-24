using System.Collections;
using UnityEngine;
using UnityEngine.UI;

public class FadeIn : MonoBehaviour
{
    [SerializeField] private float startDelay = 0.5f;  // 검은 화면 유지 시간
    [SerializeField] private float fadeDuration = 1.5f; // 밝아지는 데 걸리는 시간

    private Image fadeImage;

    private void Awake()
    {
        fadeImage = GetComponent<Image>();

        // 시작할 때 완전히 검은색으로
        Color c = fadeImage.color;
        c.a = 1f;
        fadeImage.color = c;
    }

    private void Start()
    {
        StartCoroutine(FadeInRoutine());
    }

    private IEnumerator FadeInRoutine()
    {
        // 잠깐 검은 화면 유지
        yield return new WaitForSeconds(startDelay);

        float elapsed = 0f;
        Color c = fadeImage.color;

        while (elapsed < fadeDuration)
        {
            elapsed += Time.deltaTime;
            c.a = Mathf.Lerp(1f, 0f, elapsed / fadeDuration);
            fadeImage.color = c;
            yield return null;
        }

        c.a = 0f;
        fadeImage.color = c;

        // 페이드가 끝나면 클릭을 막지 않도록 비활성화
        gameObject.SetActive(false);
    }
}