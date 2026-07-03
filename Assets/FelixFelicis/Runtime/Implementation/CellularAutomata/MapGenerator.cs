using System;
using System.Globalization;
using UnityEngine;
using Random = System.Random;

namespace FelixFelicis.Runtime
{
    public class MapGenerator : MonoBehaviour
    {
        [SerializeField, Range(0,100)] private int randomFill;
        [SerializeField] private int width;
        [SerializeField] private int height;
        private string randomSeed;
        private int[,] map;

        private void Update()
        {
            if(Input.GetKeyDown(KeyCode.Space))
                GenerateMap();
        }

        private void SmoothMap()
        {
            for (int i = 0; i < width; i++)
            {
                for (int j = 0; j < height; j++)
                {
                    int surroundingWallCount = GetSurroundingWallCount(i, j);
                    if (surroundingWallCount > 4)
                        map[i, j] = 1;
                    else if (surroundingWallCount < 4)
                        map[i, j] = 0;
                }
            }
        }

        private int GetSurroundingWallCount(int x, int y)
        {
            int cnt = 0;
            for (int i = x - 1; i <= x + 1; i++)
            {
                for (int j = y - 1; j <= y + 1; j++)
                {
                    if (i >= 0 && i < width && j >= 0 && j < height)
                    {
                        if (i != x || j != y)
                            cnt += map[i, j];
                    }
                    else cnt++;
                }
            }
            return cnt;
        }

        private void GenerateMap()
        {
            map = new int[width, height];
            randomSeed = Time.time.ToString(CultureInfo.InvariantCulture);
            
            Random rng = new Random(randomSeed.GetHashCode());
            for (int i = 0; i < width; i++)
            {
                for (int j = 0; j < height; j++)
                {
                    if(i == 0 || i == width - 1 || j == 0 || j == height - 1)
                        map[i, j] = 1;
                    else
                        map[i, j] = rng.Next(0, 100) < randomFill ? 1 : 0;
                }
            }
            
            for(int i = 0; i < 5; i++)
                SmoothMap();
        }

        private void OnDrawGizmos()
        {
            if (map == null) return;
            for (int i = 0; i < width; i++)
            {
                for (int j = 0; j < height; j++)
                {
                    Gizmos.color = map[i,j] == 1 ? Color.black : Color.white;
                    Vector3 pos = new Vector3(-width / 2f + i + 0.5f, -height / 2f + j + 0.5f, 0f);
                    Gizmos.DrawCube(pos, Vector3.one);
                }
            }
        }
    }
}
